using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;
using System.Text.Json;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// Direct inbound trigger endpoint for OpenClaw Gateway.
/// Bypasses the Azure Function Proxy — OpenClaw calls this URL directly.
/// Route: POST /api/openclaw/trigger/{nodeId}
/// </summary>
[ApiController]
[Route("api/openclaw")]
public class OpenClawTriggerController : ControllerBase
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly NodeExecutionManager _executionManager;
    private readonly ILogger<OpenClawTriggerController> _logger;

    public OpenClawTriggerController(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        NodeExecutionManager executionManager,
        ILogger<OpenClawTriggerController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _executionManager = executionManager;
        _logger = logger;
    }

    /// <summary>
    /// Receive a call from OpenClaw Gateway and trigger the connected S2G workflow.
    /// Body (all fields optional):
    /// {
    ///   "prompt":      "user message",
    ///   "session_key": "optional session id",
    ///   "data":        { any extra key/value pairs }
    /// }
    /// The response returns the value of the output field named by TriggerResponseField
    /// (default "AIResponse") plus the full OutputData map.
    /// </summary>
    [HttpPost("trigger/{nodeId}")]
    public async Task<IActionResult> Trigger(string nodeId, [FromBody] OpenClawTriggerRequest? body)
    {
        if (!Guid.TryParse(nodeId, out var nodeGuid))
            return BadRequest(new { error = "Invalid node ID format" });

        // Look up node — must be OpenClaw type
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var node = await db.WorkflowNodes
            .FirstOrDefaultAsync(n => n.Id == nodeGuid && n.NodeType == "OpenClaw");

        if (node == null)
            return NotFound(new { error = $"OpenClaw trigger node {nodeId} not found" });

        // Parse config for trigger mode & optional secret
        OpenClawTriggerConfig config;
        try { config = JsonSerializer.Deserialize<OpenClawTriggerConfig>(node.Configuration ?? "{}") ?? new(); }
        catch { config = new(); }

        if (!config.Mode.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Node is not in Trigger mode" });

        // Optional secret auth
        if (!string.IsNullOrEmpty(config.TriggerSecret))
        {
            var provided = Request.Headers["x-openclaw-secret"].FirstOrDefault()
                        ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
            if (provided != config.TriggerSecret)
            {
                _logger.LogWarning("OpenClaw trigger auth failed for node {NodeId}", nodeId);
                return Unauthorized(new { error = "Invalid trigger secret" });
            }
        }

        // Check the workflow is running
        if (!_executionManager.IsRunning(nodeGuid))
            return BadRequest(new { error = "OpenClaw trigger node is not running. Start the workflow first." });

        // Build input data dictionary
        var requestId = Guid.NewGuid();
        var inputData = new Dictionary<string, object?>
        {
            ["RequestId"]  = requestId,
            ["Prompt"]     = body?.Prompt ?? "",
            ["SessionKey"] = body?.SessionKey ?? "",
        };

        // Flatten extra data fields
        if (body?.Data != null)
        {
            foreach (var kvp in body.Data)
                inputData[kvp.Key] = kvp.Value.ToString();
        }

        // Register TCS so we can wait for workflow output
        var tcs = new TaskCompletionSource<HttpResponseData>();
        _executionManager.RegisterPendingRequest(requestId, tcs);

        // Fire the workflow
        _executionManager.TriggerNodeExecution(nodeGuid, inputData);

        _logger.LogInformation("OpenClaw trigger fired for node {NodeId}, request {RequestId}", nodeId, requestId);

        // Wait for completion or timeout
        var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task;

        if (!completed)
        {
            _logger.LogWarning("OpenClaw trigger timed out for node {NodeId}, request {RequestId}", nodeId, requestId);
            return Ok(new OpenClawTriggerResponse
            {
                Response = "",
                TimedOut = true,
                OutputData = new()
            });
        }

        var httpResult = await tcs.Task;

        // Try to parse the body as JSON OutputData
        Dictionary<string, object?>? outputData = null;
        if (!string.IsNullOrEmpty(httpResult.Body))
        {
            try { outputData = JsonSerializer.Deserialize<Dictionary<string, object?>>(httpResult.Body); }
            catch { outputData = new Dictionary<string, object?> { ["raw"] = httpResult.Body }; }
        }
        outputData ??= new();

        // Pick the primary response field
        var responseField = string.IsNullOrEmpty(config.TriggerResponseField) ? "AIResponse" : config.TriggerResponseField;
        var responseText = outputData.TryGetValue(responseField, out var rv) ? rv?.ToString() ?? "" : httpResult.Body;

        return Ok(new OpenClawTriggerResponse
        {
            Response   = responseText,
            TimedOut   = false,
            OutputData = outputData
        });
    }

    /// <summary>Health check — lets OpenClaw verify the S2G endpoint is reachable.</summary>
    [HttpGet("health")]
    public IActionResult Health() =>
        Ok(new { status = "healthy", service = "S2G OpenClaw Trigger", timestamp = DateTime.UtcNow });
}

// ─── DTOs ────────────────────────────────────────────────────────────────────

public class OpenClawTriggerRequest
{
    public string? Prompt     { get; set; }
    public string? SessionKey { get; set; }
    /// <summary>Arbitrary extra key/value pairs passed into the workflow as input data.</summary>
    public Dictionary<string, JsonElement>? Data { get; set; }
}

public class OpenClawTriggerResponse
{
    /// <summary>The primary text response (value of TriggerResponseField output).</summary>
    public string Response { get; set; } = "";
    /// <summary>Full output data map from the downstream workflow execution.</summary>
    public Dictionary<string, object?> OutputData { get; set; } = new();
    public bool TimedOut { get; set; }
}

/// <summary>Subset of OpenClawConfig properties needed for trigger validation.</summary>
internal class OpenClawTriggerConfig
{
    public string Mode                { get; set; } = "Action";
    public string? TriggerSecret      { get; set; }
    public string? TriggerResponseField { get; set; } = "AIResponse";
    public int TimeoutSeconds         { get; set; } = 300;
}
