using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class PdfOcrNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public PdfOcrNode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "PdfOcr";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<PdfOcrConfig>(node.Configuration ?? "{}") ?? new PdfOcrConfig();
        double executionCost = 0;

        // Preserve original document source for config restoration
        var originalDocumentSource = config.DocumentSource;

        // Fetch API Key from config or UserSecretService (uses same key as Mistral AI)
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "Mistral_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            Log(node, NodeLogLevel.Error, "Mistral API Key is missing. Please configure it in Settings.");
            return new NodeExecutionResult { Success = false, ErrorMessage = "Mistral API Key is missing for this user. Please configure it in Settings." };
        }

        // Resolve placeholders in document source
        var documentSource = ResolvePlaceholders(config.DocumentSource ?? "", inputData);

        if (string.IsNullOrWhiteSpace(documentSource))
        {
            Log(node, NodeLogLevel.Error, "Document source is empty. Please provide a PDF URL or Base64 content.");
            return new NodeExecutionResult { Success = false, ErrorMessage = "Document source is empty." };
        }

        // Build request body based on input type
        object documentObject;
        if (config.InputType == "Base64" || IsBase64String(documentSource))
        {
            documentObject = new
            {
                type = "base64",
                data = documentSource
            };
        }
        else
        {
            // Assume URL
            documentObject = new
            {
                type = "document_url",
                document_url = documentSource
            };
        }

        var requestBody = new
        {
            model = "mistral-ocr-latest",
            document = documentObject,
            include_image_base64 = config.IncludeImages
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/ocr")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        // Log request details
        var requestInfo = JsonSerializer.Serialize(new
        {
            Model = "mistral-ocr-latest",
            InputType = config.InputType,
            DocumentSourceLength = documentSource.Length,
            IncludeImages = config.IncludeImages,
            ExtractTables = config.ExtractTables
        }, new JsonSerializerOptions { WriteIndented = true });
        Log(node, NodeLogLevel.Info, "Sending OCR request to Mistral", requestInfo);

        try
        {
            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log(node, NodeLogLevel.Error, $"Mistral OCR API returned {(int)response.StatusCode}: {response.ReasonPhrase}", responseContent);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"Mistral OCR API error ({(int)response.StatusCode}): {responseContent}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

            // Extract pages from response
            var pages = new List<OcrPage>();
            var plainTextBuilder = new StringBuilder();
            var markdownBuilder = new StringBuilder();
            int pageCount = 0;

            if (jsonResponse.TryGetProperty("pages", out var pagesArray))
            {
                foreach (var page in pagesArray.EnumerateArray())
                {
                    pageCount++;
                    var pageMarkdown = page.TryGetProperty("markdown", out var md) ? md.GetString() ?? "" : "";
                    
                    markdownBuilder.AppendLine(pageMarkdown);
                    plainTextBuilder.AppendLine(StripMarkdown(pageMarkdown));
                    
                    pages.Add(new OcrPage
                    {
                        PageIndex = pageCount - 1,
                        Markdown = pageMarkdown
                    });
                }
            }

            var plainText = plainTextBuilder.ToString().Trim();
            var markdownContent = markdownBuilder.ToString().Trim();

            // Extract tables if requested
            string tablesJson = "[]";
            if (config.ExtractTables)
            {
                var tables = ExtractTablesFromMarkdown(markdownContent);
                tablesJson = JsonSerializer.Serialize(tables);
            }

            // Calculate cost: $1 per 1000 pages = $0.001 per page
            executionCost = pageCount * 0.001;

            // Update config with accumulated cost (using InputTokens field for page count)
            config.Cost += executionCost;
            config.InputTokens += pageCount;  // Repurposed: stores PageCount for AI Cost panel

            // Restore original document source
            config.DocumentSource = originalDocumentSource;
            node.Configuration = JsonSerializer.Serialize(config);
            _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);

            // Log success
            var detail = JsonSerializer.Serialize(new
            {
                PageCount = pageCount,
                TextLength = plainText.Length,
                MarkdownLength = markdownContent.Length,
                TablesExtracted = config.ExtractTables,
                RunCost = executionCost,
                TotalCost = config.Cost
            }, new JsonSerializerOptions { WriteIndented = true });
            Log(node, NodeLogLevel.Info, $"OCR completed: {pageCount} page(s), ${executionCost:F5}", detail);

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "Text", plainText },
                    { "Markdown", markdownContent },
                    { "Tables", tablesJson },
                    { "PageCount", pageCount },
                    { "Cost", executionCost }
                }
            };
        }
        catch (TaskCanceledException ex)
        {
            Log(node, NodeLogLevel.Error, $"Request timeout: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Request timeout: {ex.Message}" };
        }
        catch (HttpRequestException ex)
        {
            Log(node, NodeLogLevel.Error, $"Network error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Network error: {ex.Message}" };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Mistral OCR error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Mistral OCR error: {ex.Message}" };
        }
    }

    public override List<string> GetOutputParameters()
    {
        return new List<string> { "Text", "Markdown", "Tables", "PageCount", "Cost" };
    }

    /// <summary>
    /// Checks if a string is likely Base64 encoded.
    /// </summary>
    private static bool IsBase64String(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        // Quick heuristic: Base64 strings are usually long and contain only valid chars
        if (s.Length < 100) return false;
        if (s.StartsWith("http://") || s.StartsWith("https://")) return false;
        
        // Check for valid Base64 characters
        return Regex.IsMatch(s, @"^[A-Za-z0-9+/=]+$") && s.Length % 4 == 0;
    }

    /// <summary>
    /// Strips markdown formatting to get plain text.
    /// </summary>
    private static string StripMarkdown(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        
        var text = markdown;
        // Remove headers
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        // Remove bold/italic
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = Regex.Replace(text, @"\*([^*]+)\*", "$1");
        text = Regex.Replace(text, @"__([^_]+)__", "$1");
        text = Regex.Replace(text, @"_([^_]+)_", "$1");
        // Remove links
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        // Remove images
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        // Remove code blocks
        text = Regex.Replace(text, @"```[^`]*```", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        
        return text.Trim();
    }

    /// <summary>
    /// Extracts tables from markdown content as structured JSON objects.
    /// </summary>
    private static List<ExtractedTable> ExtractTablesFromMarkdown(string markdown)
    {
        var tables = new List<ExtractedTable>();
        if (string.IsNullOrEmpty(markdown)) return tables;

        // Match markdown tables: lines starting with | and containing |
        var tablePattern = @"(\|[^\n]+\|\r?\n)+";
        var matches = Regex.Matches(markdown, tablePattern);

        int tableIndex = 0;
        foreach (Match match in matches)
        {
            var tableText = match.Value.Trim();
            var lines = tableText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            if (lines.Length < 2) continue;  // Need at least header + separator or data row

            var table = new ExtractedTable
            {
                TableIndex = tableIndex++,
                Headers = new List<string>(),
                Rows = new List<List<string>>()
            };

            bool headerParsed = false;
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;
                
                // Skip separator lines (---|---|---)
                if (Regex.IsMatch(trimmedLine, @"^\|[\s\-:|]+\|$")) continue;

                var cells = ParseTableRow(trimmedLine);
                
                if (!headerParsed)
                {
                    table.Headers = cells;
                    headerParsed = true;
                }
                else
                {
                    table.Rows.Add(cells);
                }
            }

            if (table.Headers.Count > 0)
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    /// <summary>
    /// Parses a markdown table row into cells.
    /// </summary>
    private static List<string> ParseTableRow(string row)
    {
        var cells = new List<string>();
        
        // Remove leading and trailing |
        var trimmed = row.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);
        
        // Split by |
        var parts = trimmed.Split('|');
        foreach (var part in parts)
        {
            cells.Add(part.Trim());
        }
        
        return cells;
    }

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        
        var result = template;
        var placeholderRegex = new Regex(@"\{\{([^}]+)\}\}");
        result = placeholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            
            // 1. Try exact match
            if (data.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? "";
            
            // 2. Try without node prefix
            var shortKey = key.Contains('.') ? key.Split('.').Last() : key;
            if (data.TryGetValue(shortKey, out var shortValue) && shortValue != null)
                return shortValue.ToString() ?? "";
            
            // 3. Try to find key in any prefixed format
            foreach (var kvp in data)
            {
                if (kvp.Key.EndsWith("." + key) || kvp.Key.EndsWith("." + shortKey))
                {
                    return kvp.Value?.ToString() ?? "";
                }
            }
            
            return match.Value; // Return original if not found
        });
        
        return result;
    }
}

public class PdfOcrConfig
{
    public string DocumentSource { get; set; } = string.Empty;
    public string InputType { get; set; } = "Url";  // "Url" or "Base64"
    public bool ExtractTables { get; set; } = true;
    public bool IncludeImages { get; set; } = false;
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    
    // Cost tracking (compatible with AI Cost panel)
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;   // Repurposed: PageCount
    public long OutputTokens { get; set; } = 0;  // Reserved for future use
}

internal class OcrPage
{
    public int PageIndex { get; set; }
    public string Markdown { get; set; } = "";
}

internal class ExtractedTable
{
    public int TableIndex { get; set; }
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
}
