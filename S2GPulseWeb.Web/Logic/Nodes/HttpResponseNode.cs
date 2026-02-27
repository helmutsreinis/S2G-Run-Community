using System.Collections.Generic;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class HttpResponseNode : BaseNodeExecutor
{
    public HttpResponseNode(NodeExecutionManager executionManager) : base(executionManager)
    {
    }

    public override string NodeType => "HttpResponse";

    public override List<string> GetOutputParameters() => new();

    protected override Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        try
        {
            var config = JsonSerializer.Deserialize<HttpResponseConfig>(node.Configuration ?? "{}") ?? new();
            
            // We need a RequestId to send a response. 
            // This would have been passed down from the HttpListener trigger.
            // Note: RequestId may come as a Guid, string, or JsonElement depending on how it was passed through the chain.
            Guid? requestId = null;
            if (inputData.TryGetValue("RequestId", out var ridObj))
            {
                if (ridObj is Guid g)
                    requestId = g;
                else if (ridObj is string s && Guid.TryParse(s, out var parsedGuid))
                    requestId = parsedGuid;
                else if (ridObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(je.GetString(), out var jeGuid))
                    requestId = jeGuid;
            }

            if (requestId.HasValue)
            {
                // DEBUG: Log available input keys to diagnose placeholder resolution
                var availableKeys = string.Join(", ", inputData.Keys.Take(20));
                Log(node, NodeLogLevel.Debug, "Available input data keys", $"Keys: [{availableKeys}]");
                
                // Resolve placeholders in the body
                var resolvedBody = ResolvePlaceholders(config.Body ?? "", inputData);
                
                var response = new HttpResponseData
                {
                    StatusCode = config.StatusCode == 0 ? 200 : config.StatusCode,
                    Body = resolvedBody,
                    ContentType = config.ContentType ?? "text/plain",
                    Headers = config.Headers ?? new()
                };

                var detail = JsonSerializer.Serialize(new
                {
                    response.StatusCode,
                    response.ContentType,
                    response.Body,
                    response.Headers
                }, new JsonSerializerOptions { WriteIndented = true });

                _executionManager.EmitResponse(requestId.Value, response);
                Log(node, NodeLogLevel.Info, $"Sent response {response.StatusCode} for request {requestId.Value}", detail);
                return Task.FromResult(new NodeExecutionResult { Success = true });
            }
            else
            {
                var error = $"Missing or invalid RequestId. HttpResponse node must be triggered by an HttpListener. Received: {ridObj?.GetType().Name ?? "null"}";
                Log(node, NodeLogLevel.Error, error);
                return Task.FromResult(new NodeExecutionResult { Success = false, ErrorMessage = error });
            }
        }
        catch (Exception ex)
        {
            var configStr = node.Configuration ?? "{}";
            Log(node, NodeLogLevel.Error, $"Failed to deserialize configuration (Length: {configStr.Length}): {ex.Message}. Configuration: {configStr}");
            return Task.FromResult(new NodeExecutionResult { Success = false, ErrorMessage = ex.Message });
        }
    }

    /// <summary>
    /// Resolve {{NodeName.PropertyName}} placeholders from input data
    /// </summary>
    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return template;

        // Match {{NodeName.PropertyName}} pattern - supports spaces in node names
        return Regex.Replace(template, @"\{\{([^}]+)\}\}", match =>
        {
            var fullKey = match.Groups[1].Value.Trim();
            
            // Try direct match first
            if (data.TryGetValue(fullKey, out var directValue) && directValue != null)
            {
                return ConvertToString(directValue);
            }
            
            // Try NodeName.Property format
            var lastDotIndex = fullKey.LastIndexOf('.');
            if (lastDotIndex > 0)
            {
                var nodeName = fullKey.Substring(0, lastDotIndex);
                var propName = fullKey.Substring(lastDotIndex + 1);
                
                // Try exact key with node.prop format
                var key = $"{nodeName}.{propName}";
                if (data.TryGetValue(key, out var value) && value != null)
                {
                    return ConvertToString(value);
                }
                
                // Try just the property name (for immediate upstream)
                if (data.TryGetValue(propName, out var propValue) && propValue != null)
                {
                    return ConvertToString(propValue);
                }
            }
            
            // Return original if not found
            return match.Value;
        });
    }

    private string ConvertToString(object value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString() ?? "",
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => je.GetRawText()
            };
        }
        return value?.ToString() ?? "";
    }

    public class HttpResponseConfig
    {
        public int StatusCode { get; set; } = 200;
        public string? Body { get; set; }
        public string? ContentType { get; set; } = "text/plain";
        public Dictionary<string, string>? Headers { get; set; }
    }
}

