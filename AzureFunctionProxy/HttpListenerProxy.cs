using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Web;

namespace S2GPulseWeb.AzureFunctionProxy;

/// <summary>
/// Azure Function that proxies HTTP requests to S2G Run listener nodes.
/// Supports wildcard subdomain routing (e.g., {NodeId}.listener.mydomain.com)
/// </summary>
public class HttpListenerProxy
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _s2gWebAppUrl;

    public HttpListenerProxy(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
    {
        _logger = loggerFactory.CreateLogger<HttpListenerProxy>();
        _httpClient = httpClientFactory.CreateClient();
        _s2gWebAppUrl = Environment.GetEnvironmentVariable("S2G_WEB_APP_URL") 
            ?? "https://s2gpulseweb-web.internal";
    }

    /// <summary>
    /// Wildcard HTTP trigger that captures all requests and routes them based on subdomain/headers
    /// </summary>
    [Function("WildcardProxy")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "delete", "patch", Route = "{*path}")] 
        HttpRequestData req,
        string? path)
    {
        // Extract Node ID from subdomain, header, or query parameter
        var nodeId = ExtractNodeId(req);
        
        if (nodeId == null)
        {
            _logger.LogWarning("Could not extract Node ID from request. Host: {Host}, Headers: {Headers}", 
                req.Url.Host, string.Join(", ", req.Headers.Select(h => h.Key)));
            return await CreateErrorResponse(req, HttpStatusCode.BadRequest, 
                "Could not extract Node ID. Use subdomain (nodeId.domain.com), X-S2G-Node-Id header, or ?nodeId= parameter.");
        }

        _logger.LogInformation("Routing {Method} request to Node ID: {NodeId}, Path: {Path}", 
            req.Method, nodeId, path ?? "/");

        try
        {
            // Read request body using ReadAsStringAsync - recommended for Azure Functions Isolated Worker
            var bodyContent = await req.ReadAsStringAsync();
            
            _logger.LogInformation("Body read via ReadAsStringAsync: Length={Length}", 
                bodyContent?.Length ?? 0);

            // Build proxy request object
            var proxyRequest = new ListenerProxyRequest
            {
                NodeId = nodeId,
                Method = req.Method,
                Path = path ?? "/",
                QueryString = req.Url.Query,
                Headers = req.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
                Body = bodyContent
            };

            // Forward request to S2G Web internal API
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(proxyRequest),
                System.Text.Encoding.UTF8,
                "application/json");

            // Add API key for authentication (if configured)
            var apiKey = Environment.GetEnvironmentVariable("S2G_API_KEY");
            if (!string.IsNullOrEmpty(apiKey))
            {
                jsonContent.Headers.Add("X-S2G-Api-Key", apiKey);
            }

            var response = await _httpClient.PostAsync(
                $"{_s2gWebAppUrl}/api/listener/proxy",
                jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("S2G Web API returned error {StatusCode}: {Error}", 
                    response.StatusCode, errorContent);
                return await CreateErrorResponse(req, response.StatusCode, 
                    $"Error from S2G Web: {errorContent}");
            }

            // Parse response from S2G Web (use case-insensitive as ASP.NET Core serializes as camelCase)
            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("S2G Web response length: {Length} bytes", responseContent.Length);
            
            var jsonOptions = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            };
            var listenerResponse = JsonSerializer.Deserialize<ListenerProxyResponse>(responseContent, jsonOptions);

            if (listenerResponse == null)
            {
                _logger.LogError("Failed to deserialize response: {Content}", responseContent.Substring(0, Math.Min(500, responseContent.Length)));
                return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, 
                    "Invalid response from S2G Web");
            }

            _logger.LogInformation("Parsed response: StatusCode={StatusCode}, BodyLength={BodyLength}, ContentType={ContentType}",
                listenerResponse.StatusCode, listenerResponse.Body?.Length ?? 0, listenerResponse.ContentType);

            // Create HTTP response
            var httpResponse = req.CreateResponse();
            // Ensure valid status code (default to 200 if 0 or invalid)
            var statusCode = listenerResponse.StatusCode > 0 ? listenerResponse.StatusCode : 200;
            httpResponse.StatusCode = (HttpStatusCode)statusCode;
            httpResponse.Headers.Add("Content-Type", listenerResponse.ContentType ?? "application/json");
            
            // Add custom headers from workflow
            if (listenerResponse.Headers != null)
            {
                foreach (var header in listenerResponse.Headers)
                {
                    try
                    {
                        httpResponse.Headers.Add(header.Key, header.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not add header {Header}: {Error}", header.Key, ex.Message);
                    }
                }
            }
            
            var bodyToWrite = listenerResponse.Body ?? "";
            _logger.LogInformation("Writing body to response: {Length} characters", bodyToWrite.Length);
            await httpResponse.WriteStringAsync(bodyToWrite);
            
            _logger.LogInformation("Successfully proxied request to Node {NodeId}, returned {StatusCode}", 
                nodeId, listenerResponse.StatusCode);
            
            return httpResponse;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error proxying request to Node {NodeId}", nodeId);
            return await CreateErrorResponse(req, HttpStatusCode.BadGateway, 
                $"Could not reach S2G Web application: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error proxying request to Node {NodeId}", nodeId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, 
                $"Error processing request: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract Node ID from request using multiple strategies:
    /// 1. Custom header (X-S2G-Node-Id) - highest priority
    /// 2. Query parameter (nodeId) - common for testing
    /// 3. Subdomain (e.g., {guid}.listener.mydomain.com) - only for custom domains
    /// </summary>
    private string? ExtractNodeId(HttpRequestData req)
    {
        // Strategy 1: Extract from custom header (highest priority)
        if (req.Headers.TryGetValues("X-S2G-Node-Id", out var headerValues))
        {
            var headerValue = headerValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(headerValue))
            {
                return headerValue;
            }
        }

        // Strategy 2: Extract from query parameter
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var nodeIdParam = query["nodeId"];
        if (!string.IsNullOrEmpty(nodeIdParam))
        {
            return nodeIdParam;
        }

        // Strategy 3: Extract from subdomain (only for custom domains, not Azure defaults)
        var host = req.Url.Host;
        
        // Skip subdomain extraction for Azure default domains
        if (host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = host.Split('.');
        if (parts.Length > 2)
        {
            var subdomain = parts[0];
            // Only use subdomain if it looks like a valid GUID
            if (Guid.TryParse(subdomain, out _))
            {
                return subdomain;
            }
        }

        return null;
    }

    private async Task<HttpResponseData> CreateErrorResponse(
        HttpRequestData req, 
        HttpStatusCode statusCode, 
        string message)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json");
        
        var errorObj = new { error = message, statusCode = (int)statusCode };
        await response.WriteStringAsync(JsonSerializer.Serialize(errorObj));
        
        return response;
    }
}

// DTOs for communication with S2G Web

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
    public int StatusCode { get; set; } = 200; // Default to 200 OK
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}
