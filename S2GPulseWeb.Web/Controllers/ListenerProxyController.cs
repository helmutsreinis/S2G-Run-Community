using Microsoft.AspNetCore.Mvc;
using S2GPulseWeb.Web.Logic;
using S2GPulseWeb.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// API controller that receives proxy requests from Azure Function and routes them to listener nodes.
/// This enables HTTP Listener nodes to work in containerized environments where direct port binding is not feasible.
/// </summary>
[ApiController]
[Route("api/listener")]
public class ListenerProxyController : ControllerBase
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly NodeExecutionManager _executionManager;
    private readonly ILogger<ListenerProxyController> _logger;

    public ListenerProxyController(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        NodeExecutionManager executionManager,
        ILogger<ListenerProxyController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _executionManager = executionManager;
        _logger = logger;
    }

    /// <summary>
    /// Receives a proxied HTTP request from Azure Function and routes it to the appropriate listener node.
    /// </summary>
    [HttpPost("proxy")]
    public async Task<IActionResult> ProxyRequest([FromBody] ListenerProxyRequest request)
    {
        try
        {
            // Validate Node ID
            if (!Guid.TryParse(request.NodeId, out var nodeId))
            {
                _logger.LogWarning("Invalid Node ID format: {NodeId}", request.NodeId);
                return BadRequest(new { error = "Invalid Node ID format" });
            }

            _logger.LogInformation("Proxying {Method} request to Node {NodeId}, Path: {Path}", 
                request.Method, nodeId, request.Path);

            // Find the listener node (HttpListener or Remote node)
            using var db = await _dbContextFactory.CreateDbContextAsync();
            var node = await db.WorkflowNodes
                .FirstOrDefaultAsync(n => n.Id == nodeId && (n.NodeType == "HttpListener" || n.NodeType == "Remote"));

            if (node == null)
            {
                _logger.LogWarning("Listener node {NodeId} not found (must be HttpListener or Remote type)", nodeId);
                return NotFound(new { error = $"Listener node {nodeId} not found" });
            }

            // Proxy mode is now the default - no need to check config.UseProxyMode

            // Check if the workflow is running (listener is started)
            if (!_executionManager.IsRunning(nodeId))
            {
                _logger.LogWarning("Listener node {NodeId} is not running", nodeId);
                return BadRequest(new { error = "Listener node is not running. Start the workflow first." });
            }

            // Create request data dictionary (same format as direct HTTP listener)
            var requestId = Guid.NewGuid();
            var requestData = new Dictionary<string, object?>
            {
                ["RequestId"] = requestId,
                ["Method"] = request.Method,
                ["Path"] = request.Path,
                ["Body"] = request.Body,
                ["Headers"] = request.Headers,
                ["HeadersJson"] = JsonSerializer.Serialize(request.Headers),
                ["QueryParams"] = ParseQueryString(request.QueryString),
                ["QueryParamsJson"] = JsonSerializer.Serialize(ParseQueryString(request.QueryString))
            };

            // Also add individual query params for direct placeholder access
            var queryParams = ParseQueryString(request.QueryString);
            foreach (var param in queryParams)
            {
                requestData[param.Key] = param.Value;
            }

            // Store samples for config update (body detection)
            if (!string.IsNullOrEmpty(request.Body))
            {
                requestData["_BodySample"] = request.Body;
            }
            requestData["_QueryParamsSample"] = string.Join(",", queryParams.Keys);

            // Store headers sample for config update (header name detection)
            requestData["_HeadersSample"] = JsonSerializer.Serialize(request.Headers);

            // Create TaskCompletionSource to wait for response
            var tcs = new TaskCompletionSource<HttpResponseData>();
            var config = JsonSerializer.Deserialize<HttpListenerProxyConfig>(node.Configuration ?? "{}") 
                ?? new HttpListenerProxyConfig();
            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;

            // Register the pending request
            _executionManager.RegisterPendingRequest(requestId, tcs);

            // Trigger the workflow with this data
            _executionManager.TriggerNodeExecution(nodeId, requestData);

            _logger.LogInformation("Triggered workflow for request {RequestId} to node {NodeId}", requestId, nodeId);

            // Wait for response with timeout
            var timeoutTask = Task.Delay(timeoutMs);
            HttpResponseData response;

            if (await Task.WhenAny(tcs.Task, timeoutTask) == tcs.Task)
            {
                response = await tcs.Task;
                _logger.LogInformation("Request {RequestId} completed with status {StatusCode}", requestId, response.StatusCode);
            }
            else
            {
                // Timeout - use default response
                _logger.LogWarning("Request {RequestId} to node {NodeId} timed out after {Timeout}ms", 
                    requestId, nodeId, timeoutMs);
                response = new HttpResponseData
                {
                    StatusCode = config.DefaultStatusCode != 0 ? config.DefaultStatusCode : 200,
                    Body = config.DefaultResponse ?? "OK",
                    ContentType = config.ContentType ?? "text/plain"
                };
            }

            // Log what we're returning
            _logger.LogInformation("Returning response: StatusCode={StatusCode}, BodyLength={BodyLength}, ContentType={ContentType}",
                response.StatusCode, response.Body?.Length ?? 0, response.ContentType);

            // Return the response
            return Ok(new ListenerProxyResponse
            {
                StatusCode = response.StatusCode,
                Body = response.Body,
                ContentType = response.ContentType,
                Headers = response.Headers
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing proxy request for node {NodeId}", request.NodeId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Health check endpoint to verify the proxy API is reachable.
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    private Dictionary<string, string> ParseQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        var query = System.Web.HttpUtility.ParseQueryString(queryString ?? "");
        
        foreach (string? key in query.Keys)
        {
            if (key != null)
            {
                result[key] = query[key] ?? "";
            }
        }
        
        return result;
    }
}

#region DTOs

public class ListenerProxyRequest
{
    public string NodeId { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public string? QueryString { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Body { get; set; }
}

public class ListenerProxyResponse
{
    public int StatusCode { get; set; }
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>
/// Subset of HttpListenerConfig properties needed for proxy validation
/// </summary>
internal class HttpListenerProxyConfig
{
    public bool UseProxyMode { get; set; } = true; // Default is now true - proxy mode is standard
    public int DefaultStatusCode { get; set; } = 200;
    public string? DefaultResponse { get; set; }
    public string? ContentType { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
}

#endregion
