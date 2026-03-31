using System.Net.Http;
using System.Text.Json;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class HttpRequestNode : BaseNodeExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpRequestNode(IHttpClientFactory httpClientFactory, NodeExecutionManager executionManager) 
        : base(executionManager)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override string NodeType => "HttpRequest";

    public override List<string> GetOutputParameters() => new() { "StatusCode", "Body", "IsSuccess" };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<HttpRequestConfig>(node.Configuration ?? "{}") ?? new();
        
        // Build full URL with query parameters
        #pragma warning disable CS0618 // Accessing obsolete Url for backward compatibility
        var baseUrl = config.BaseUrl ?? config.Url ?? "";  // Fallback to legacy Url
        #pragma warning restore CS0618
        var fullUrl = BuildUrlWithQueryParams(baseUrl, config.QueryParams);
        
        using var client = _httpClientFactory.CreateClient();
        var method = new HttpMethod(config.Method ?? "GET");
        var request = new HttpRequestMessage(method, fullUrl);

        // Add enabled headers
        if (config.Headers != null)
        {
            foreach (var header in config.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Key)))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (!string.IsNullOrEmpty(config.Body))
        {
            request.Content = new StringContent(config.Body, System.Text.Encoding.UTF8, "application/json");
        }

        Log(node, NodeLogLevel.Info, $"Sending {config.Method} request to {fullUrl}");
        
        var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        // Standardized detailed logging
        var logDetail = JsonSerializer.Serialize(new
        {
            Request = new
            {
                Url = fullUrl,
                config.Method,
                Headers = config.Headers?.Where(h => h.Enabled).Select(h => new { h.Key, h.Value }),
                config.Body
            },
            Response = new
            {
                StatusCode = (int)response.StatusCode,
                Body = content,
                Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value))
            }
        }, new JsonSerializerOptions { WriteIndented = true });

        Log(node, NodeLogLevel.Info, $"Received response: {(int)response.StatusCode} {response.StatusCode}", logDetail);

        // Collect response headers
        var responseHeaders = new Dictionary<string, string>();
        foreach (var header in response.Headers)
        {
            responseHeaders[header.Key] = string.Join(", ", header.Value);
        }
        foreach (var header in response.Content.Headers)
        {
            responseHeaders[header.Key] = string.Join(", ", header.Value);
        }
        var responseHeadersJson = JsonSerializer.Serialize(responseHeaders);

        var output = new Dictionary<string, object?>
        {
            { "StatusCode", (int)response.StatusCode },
            { "Body", content },
            { "IsSuccess", response.IsSuccessStatusCode },
            { "ResponseHeadersJson", responseHeadersJson },
            { "_ResponseSample", content }, // For config update with sample
            { "_ResponseHeadersSample", responseHeadersJson } // For config update with header detection
        };

        // Pass through RequestId if available
        if (inputData.TryGetValue("RequestId", out var rid))
        {
            output["RequestId"] = rid;
        }

        return new NodeExecutionResult
        {
            Success = response.IsSuccessStatusCode,
            ErrorMessage = response.IsSuccessStatusCode ? null : $"HTTP Error: {response.StatusCode}",
            OutputData = output
        };
    }

    private static string BuildUrlWithQueryParams(string baseUrl, List<HttpParam>? queryParams)
    {
        if (queryParams == null || !queryParams.Any(p => p.Enabled))
            return baseUrl;

        // Don't encode values - user provides them exactly as they should appear
        // Only the key is lightly sanitized, but values are used as-is to support already-encoded values
        var enabledParams = queryParams
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key))
            .Select(p => $"{p.Key}={p.Value ?? ""}");

        var queryString = string.Join("&", enabledParams);
        if (string.IsNullOrEmpty(queryString))
            return baseUrl;

        return baseUrl.Contains('?') 
            ? $"{baseUrl}&{queryString}" 
            : $"{baseUrl}?{queryString}";
    }
}

public class HttpRequestConfig
{
    public string? BaseUrl { get; set; }  // URL without query string
    public string? Method { get; set; }
    public string? Body { get; set; }
    /// <summary>Query parameters as key-value pairs with enable flag</summary>
    public List<HttpParam>? QueryParams { get; set; }
    /// <summary>Headers as key-value pairs with enable flag</summary>
    public List<HttpHeader>? Headers { get; set; }
    /// <summary>JSON response sample from last request for dynamic property detection</summary>
    public string? LastResponseSample { get; set; }
    
    // Legacy support - will be migrated on save
    [Obsolete("Use BaseUrl instead")]
    public string? Url { get; set; }
}

/// <summary>
/// Query parameter with enable toggle (Postman-style).
/// </summary>
public class HttpParam
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// HTTP header with enable toggle (Postman-style).
/// </summary>
public class HttpHeader
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

