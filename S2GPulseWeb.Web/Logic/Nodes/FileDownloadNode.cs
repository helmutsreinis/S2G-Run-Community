using System.Net.Http;
using System.Text.Json;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Download File node - Downloads files from URLs and outputs binary content for storage nodes.
/// Supports authentication headers and outputs base64-encoded content for downstream processing.
/// </summary>
public class FileDownloadNode : BaseNodeExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;

    public FileDownloadNode(IHttpClientFactory httpClientFactory, NodeExecutionManager executionManager) 
        : base(executionManager)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override string NodeType => "FileDownload";

    public override List<string> GetOutputParameters() => new() 
    { 
        "Success", 
        "FileName", 
        "ContentType", 
        "FileSize", 
        "ContentBase64", 
        "ErrorMessage" 
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<FileDownloadConfig>(node.Configuration ?? "{}") ?? new();
        
        var url = config.Url ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "URL is required",
                OutputData = new Dictionary<string, object?>
                {
                    { "Success", false },
                    { "ErrorMessage", "URL is required" }
                }
            };
        }

        Log(node, NodeLogLevel.Info, $"Downloading file from: {url}");

        try
        {
            using var client = _httpClientFactory.CreateClient();
            
            // Set timeout if configured
            if (config.TimeoutSeconds > 0)
            {
                client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
            }

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Add custom headers (for authentication, etc.)
            if (config.Headers != null)
            {
                foreach (var header in config.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Key)))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Log(node, NodeLogLevel.Error, $"Download failed: {response.StatusCode}", errorBody);
                
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    OutputData = new Dictionary<string, object?>
                    {
                        { "Success", false },
                        { "ErrorMessage", $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}" }
                    }
                };
            }

            // Read binary content
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var contentBase64 = Convert.ToBase64String(bytes);

            // Extract filename from Content-Disposition header or URL
            var fileName = ExtractFileName(response, url, config.FileName);
            
            // Get content type
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            Log(node, NodeLogLevel.Info, 
                $"Downloaded: {fileName} ({FormatFileSize(bytes.Length)}, {contentType})",
                $"Content-Length: {bytes.Length} bytes\nContent-Type: {contentType}");

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "Success", true },
                    { "FileName", fileName },
                    { "ContentType", contentType },
                    { "FileSize", bytes.Length },
                    { "ContentBase64", contentBase64 },
                    { "ErrorMessage", null }
                }
            };
        }
        catch (TaskCanceledException)
        {
            Log(node, NodeLogLevel.Error, "Download timed out");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Download timed out",
                OutputData = new Dictionary<string, object?>
                {
                    { "Success", false },
                    { "ErrorMessage", "Download timed out" }
                }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Download error: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                OutputData = new Dictionary<string, object?>
                {
                    { "Success", false },
                    { "ErrorMessage", ex.Message }
                }
            };
        }
    }

    private static string ExtractFileName(HttpResponseMessage response, string url, string? configuredFileName)
    {
        // Use configured filename if provided
        if (!string.IsNullOrWhiteSpace(configuredFileName))
            return configuredFileName;

        // Try Content-Disposition header
        var contentDisposition = response.Content.Headers.ContentDisposition;
        if (contentDisposition?.FileName != null)
        {
            return contentDisposition.FileName.Trim('"');
        }
        if (contentDisposition?.FileNameStar != null)
        {
            return contentDisposition.FileNameStar;
        }

        // Extract from URL path
        try
        {
            var uri = new Uri(url);
            var pathFileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(pathFileName) && pathFileName.Contains('.'))
            {
                return pathFileName;
            }
        }
        catch { }

        // Default fallback
        return $"download_{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public class FileDownloadConfig
{
    /// <summary>URL to download the file from</summary>
    public string? Url { get; set; }
    
    /// <summary>Optional custom filename (overrides Content-Disposition and URL-derived name)</summary>
    public string? FileName { get; set; }
    
    /// <summary>Timeout in seconds (0 = default HttpClient timeout)</summary>
    public int TimeoutSeconds { get; set; } = 60;
    
    /// <summary>HTTP headers for authentication or custom requests</summary>
    public List<FileDownloadHeader>? Headers { get; set; }
}

public class FileDownloadHeader
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
