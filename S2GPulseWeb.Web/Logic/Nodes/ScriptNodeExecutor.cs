using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Runtime;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Executor for custom nodes using Jint JavaScript engine.
/// Provides a sandboxed execution environment with controlled APIs.
/// </summary>
public class ScriptNodeExecutor : BaseNodeExecutor
{
    private readonly CustomNodeDefinition _definition;
    private readonly IHttpClientFactory? _httpClientFactory;

    public ScriptNodeExecutor(
        NodeExecutionManager executionManager,
        CustomNodeDefinition definition,
        IHttpClientFactory? httpClientFactory = null)
        : base(executionManager)
    {
        _definition = definition;
        _httpClientFactory = httpClientFactory;
    }

    public override string NodeType => _definition.NodeTypeKey;

    public override List<string> GetOutputParameters() =>
        _definition.OutputParameters.Select(p => p.ParameterName).ToList();

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node, 
        Dictionary<string, object?> inputData, 
        string userId)
    {
        // 1. Deserialize configuration and resolve placeholders
        var config = string.IsNullOrEmpty(node.Configuration) 
            ? new Dictionary<string, object?>() 
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node.Configuration)?
                .ToDictionary(kvp => kvp.Key, kvp => GetJsonElementValue(kvp.Value)) 
            ?? new Dictionary<string, object?>();

        // Resolve placeholders in config values
        var resolvedConfig = new Dictionary<string, object?>();
        foreach (var field in _definition.InputFields)
        {
            var rawValue = config.TryGetValue(field.FieldName, out var val) 
                ? val?.ToString() ?? field.DefaultValue ?? "" 
                : field.DefaultValue ?? "";

            var resolved = field.AllowPlaceholders 
                ? ResolvePlaceholders(rawValue, inputData) 
                : rawValue;

            resolvedConfig[field.FieldName] = resolved;
            
            // Debug logging for troubleshooting
            Log(node, NodeLogLevel.Debug, $"Input '{field.FieldName}': raw='{Truncate(rawValue, 50)}', resolved='{Truncate(resolved, 50)}'");
        }

        // Apply execution delay if configured
        if (_definition.ExecutionDelayMs > 0)
        {
            await Task.Delay(_definition.ExecutionDelayMs);
        }

        // 2. Initialize Jint engine with security constraints
        var outputs = new Dictionary<string, object?>();
        var triggeredTags = new List<string>();
        var logs = new List<(NodeLogLevel Level, string Message, string? Detail)>();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_definition.TimeoutSeconds));
            
            var engine = new Engine(options => options
                .TimeoutInterval(TimeSpan.FromSeconds(_definition.TimeoutSeconds))
                .MaxStatements(100_000) // Prevent infinite loops
                .Strict()
                .CancellationToken(cts.Token)
            );

            // 3. Inject context APIs

            // Input API
            var inputApi = new InputApi(resolvedConfig, inputData);
            engine.SetValue("input", inputApi);

            // Upstream API (direct access to inputData)
            var upstreamApi = new UpstreamApi(inputData);
            engine.SetValue("upstream", upstreamApi);

            // Output API
            var outputApi = new OutputApi(outputs);
            engine.SetValue("output", outputApi);

            // Tags API
            var tagsApi = new TagsApi(triggeredTags, _definition.ConnectionTags.Select(t => t.TagName).ToList());
            engine.SetValue("tags", tagsApi);

            // Logging API
            var logApi = new LogApi(logs, node, this);
            engine.SetValue("log", logApi);

            // JSON utilities
            engine.SetValue("json", new JsonApi());

            // HTTP API (only if execution type allows)
            if (_definition.ExecutionType == CustomNodeExecutionType.HttpRequest && 
                _httpClientFactory != null)
            {
                var httpApi = new HttpApi(_httpClientFactory);
                engine.SetValue("http", httpApi);
            }

            // Delay function
            engine.SetValue("delay", new Func<int, Task>(async ms => await Task.Delay(ms)));

            // Node context
            engine.SetValue("nodeId", node.Id.ToString());
            engine.SetValue("nodeName", node.Name);
            engine.SetValue("userId", userId);

            // 4. Execute initialization script if present
            if (!string.IsNullOrWhiteSpace(_definition.InitializationScript))
            {
                engine.Execute(_definition.InitializationScript);
            }

            // 5. Execute main script
            if (!string.IsNullOrWhiteSpace(_definition.Script))
            {
                engine.Execute(_definition.Script);
            }

            // 6. Process logs according to log configuration
            foreach (var logConfig in _definition.LogConfigs.Where(l => l.IsEnabled))
            {
                switch (logConfig.LogTarget)
                {
                    case CustomLogTarget.Input when resolvedConfig.TryGetValue(logConfig.TargetName, out var inputVal):
                        Log(node, logConfig.LogLevel, 
                            FormatLogMessage(logConfig.MessageFormat, logConfig.TargetName, inputVal?.ToString() ?? "null"),
                            inputVal?.ToString());
                        break;
                    case CustomLogTarget.Output when outputs.TryGetValue(logConfig.TargetName, out var outputVal):
                        Log(node, logConfig.LogLevel,
                            FormatLogMessage(logConfig.MessageFormat, logConfig.TargetName, outputVal?.ToString() ?? "null"),
                            outputVal?.ToString());
                        break;
                }
            }

            // Flush collected logs from script
            foreach (var (level, message, detail) in logs)
            {
                Log(node, level, message, detail);
            }

            // 7. Build result
            var result = new NodeExecutionResult
            {
                Success = true,
                OutputData = outputs
            };

            // Add triggered tags to output for routing (serialize to JSON for proper placeholder resolution)
            if (triggeredTags.Any())
            {
                var serializedTags = JsonSerializer.Serialize(triggeredTags);
                result.OutputData["_TriggeredTags"] = serializedTags;
                Log(node, NodeLogLevel.Info, 
                    $"Triggered connection tags for routing: [{string.Join(", ", triggeredTags)}]",
                    $"Tags: {serializedTags}\nOutput keys: [{string.Join(", ", result.OutputData.Keys)}]");
            }
            else
            {
                Log(node, NodeLogLevel.Debug, "No connection tags triggered - all downstream connections will execute");
            }

            return result;
        }
        catch (TimeoutException)
        {
            Log(node, NodeLogLevel.Error, $"Script execution timed out after {_definition.TimeoutSeconds} seconds");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"Script execution timed out after {_definition.TimeoutSeconds} seconds"
            };
        }
        catch (StatementsCountOverflowException)
        {
            Log(node, NodeLogLevel.Error, "Script exceeded maximum statement count (possible infinite loop)");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Script exceeded maximum statement count (possible infinite loop)"
            };
        }
        catch (JavaScriptException jsEx)
        {
            Log(node, NodeLogLevel.Error, $"JavaScript error: {jsEx.Message}", jsEx.StackTrace);
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"JavaScript error: {jsEx.Message}"
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Script execution failed: {ex.Message}", ex.StackTrace);
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var result = template;

        // Handle {{placeholder}} format
        var placeholderRegex = new Regex(@"\{\{([^}]+)\}\}");
        result = placeholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value; // e.g., HttpListener.Body.access_token

            // Try exact match first (handles simple cases like {{Value}})
            if (data.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? "";

            // Parse placeholder path: NodeName.OutputProperty[.JsonPath]
            var parts = key.Split('.', 2);
            if (parts.Length >= 2)
            {
                var sourceNodeName = parts[0]; // e.g., HttpListener
                var propertyPath = parts[1];   // e.g., Body.access_token

                // Split property path to check for JSON path
                var pathParts = propertyPath.Split('.', 2);
                var outputPropertyName = pathParts[0]; // e.g., Body
                var jsonPath = pathParts.Length > 1 ? pathParts[1] : null; // e.g., access_token

                // Try to find the output property in data with node prefix
                var prefixedKey = $"{sourceNodeName}.{outputPropertyName}";
                if (data.TryGetValue(prefixedKey, out var prefixedVal) && prefixedVal != null)
                {
                    if (jsonPath != null)
                    {
                        var extracted = ExtractJsonPath(prefixedVal.ToString() ?? "", jsonPath);
                        if (extracted != null) return extracted;
                    }
                    return prefixedVal.ToString() ?? "";
                }

                // Try without node prefix (just the output property)
                if (data.TryGetValue(outputPropertyName, out var directVal) && directVal != null)
                {
                    if (jsonPath != null)
                    {
                        var extracted = ExtractJsonPath(directVal.ToString() ?? "", jsonPath);
                        if (extracted != null) return extracted;
                    }
                    return directVal.ToString() ?? "";
                }
            }

            // Fallback: Try without node prefix using the last segment
            var shortKey = key.Contains('.') ? key.Split('.').Last() : key;
            if (data.TryGetValue(shortKey, out var shortValue) && shortValue != null)
                return shortValue.ToString() ?? "";

            // Try to find key in any prefixed format
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

    /// <summary>
    /// Extract a value from JSON using a dot-notation path (e.g., "access_token" or "nested.property")
    /// </summary>
    private static string? ExtractJsonPath(string json, string path)
    {
        if (string.IsNullOrEmpty(json)) return null;
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;

            // Navigate through the path
            foreach (var part in path.Split('.'))
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var child))
                {
                    element = child;
                }
                else
                {
                    return null; // Path not found
                }
            }

            // Return the value based on its type
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => element.GetRawText() // For objects/arrays, return raw JSON
            };
        }
        catch
        {
            return null; // Invalid JSON or path
        }
    }

    private string FormatLogMessage(string? format, string targetName, string value)
    {
        if (string.IsNullOrEmpty(format))
            return $"{targetName}: {value}";

        return format
            .Replace("{name}", targetName)
            .Replace("{value}", value);
    }

    /// <summary>
    /// Extracts the actual value from a JsonElement.
    /// </summary>
    private static object? GetJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// Truncates a string for logging purposes.
    /// </summary>
    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        if (value.Length <= maxLength) return value;
        return value.Substring(0, maxLength) + "...";
    }

    #region JavaScript API Classes

    private class InputApi
    {
        private readonly Dictionary<string, object?> _config;
        private readonly Dictionary<string, object?> _inputData;

        public InputApi(Dictionary<string, object?> config, Dictionary<string, object?> inputData)
        {
            _config = config;
            _inputData = inputData;
        }

        public object? get(string fieldName)
        {
            if (_config.TryGetValue(fieldName, out var val))
                return val;
            return null;
        }

        public Dictionary<string, object?> all() => new(_config);
    }

    private class UpstreamApi
    {
        private readonly Dictionary<string, object?> _inputData;

        public UpstreamApi(Dictionary<string, object?> inputData)
        {
            _inputData = inputData;
        }

        public object? get(string key)
        {
            // Try exact match first
            if (_inputData.TryGetValue(key, out var val))
                return val;

            // Parse key for JSON path: NodeName.OutputProperty[.JsonPath]
            var parts = key.Split('.', 2);
            if (parts.Length >= 2)
            {
                var sourceNodeName = parts[0]; // e.g., HttpListener
                var propertyPath = parts[1];   // e.g., Body.access_token

                // Split property path to check for JSON path
                var pathParts = propertyPath.Split('.', 2);
                var outputPropertyName = pathParts[0]; // e.g., Body
                var jsonPath = pathParts.Length > 1 ? pathParts[1] : null; // e.g., access_token

                // Try to find the output property in data with node prefix
                var prefixedKey = $"{sourceNodeName}.{outputPropertyName}";
                if (_inputData.TryGetValue(prefixedKey, out var prefixedVal) && prefixedVal != null)
                {
                    if (jsonPath != null)
                    {
                        var extracted = ExtractJsonPath(prefixedVal.ToString() ?? "", jsonPath);
                        if (extracted != null) return extracted;
                    }
                    return prefixedVal;
                }

                // Try without node prefix (just the output property)
                if (_inputData.TryGetValue(outputPropertyName, out var directVal) && directVal != null)
                {
                    if (jsonPath != null)
                    {
                        var extracted = ExtractJsonPath(directVal.ToString() ?? "", jsonPath);
                        if (extracted != null) return extracted;
                    }
                    return directVal;
                }
            }

            // Try short key (last segment)
            var shortKey = key.Contains('.') ? key.Split('.').Last() : key;
            if (_inputData.TryGetValue(shortKey, out var shortVal))
                return shortVal;

            // Try suffix match
            foreach (var kvp in _inputData)
            {
                if (kvp.Key.EndsWith("." + key) || kvp.Key.EndsWith("." + shortKey))
                    return kvp.Value;
            }

            return null;
        }

        public Dictionary<string, object?> all() => new(_inputData);
        
        /// <summary>
        /// Extract a value from JSON using a dot-notation path
        /// </summary>
        private static string? ExtractJsonPath(string json, string path)
        {
            if (string.IsNullOrEmpty(json)) return null;
            
            try
            {
                using var doc = JsonDocument.Parse(json);
                var element = doc.RootElement;

                foreach (var part in path.Split('.'))
                {
                    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var child))
                    {
                        element = child;
                    }
                    else
                    {
                        return null;
                    }
                }

                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => element.GetRawText()
                };
            }
            catch
            {
                return null;
            }
        }
    }

    private class OutputApi
    {
        private readonly Dictionary<string, object?> _outputs;

        public OutputApi(Dictionary<string, object?> outputs)
        {
            _outputs = outputs;
        }

        public void set(string name, object? value)
        {
            // Auto-serialize complex types to JSON for proper placeholder resolution
            if (value != null && IsComplexType(value))
            {
                _outputs[name] = JsonSerializer.Serialize(value);
            }
            else
            {
                _outputs[name] = value;
            }
        }

        public void setJson(string name, object? value)
        {
            _outputs[name] = value != null 
                ? JsonSerializer.Serialize(value) 
                : null;
        }
        
        private static bool IsComplexType(object value)
        {
            var type = value.GetType();
            // Check for Dictionary, Array, List, or any collection
            return type.IsArray 
                || value is System.Collections.IDictionary 
                || value is System.Collections.IList
                || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>));
        }
    }

    private class TagsApi
    {
        private readonly List<string> _triggeredTags;
        private readonly List<string> _validTags;

        public TagsApi(List<string> triggeredTags, List<string> validTags)
        {
            _triggeredTags = triggeredTags;
            _validTags = validTags;
        }

        public void trigger(string tagName)
        {
            if (!_triggeredTags.Contains(tagName))
            {
                _triggeredTags.Add(tagName);
            }
        }

        public bool isValid(string tagName) => _validTags.Contains(tagName);
    }

    private class LogApi
    {
        private readonly List<(NodeLogLevel Level, string Message, string? Detail)> _logs;
        private readonly WorkflowNode _node;
        private readonly ScriptNodeExecutor _executor;

        public LogApi(List<(NodeLogLevel Level, string Message, string? Detail)> logs, 
            WorkflowNode node, ScriptNodeExecutor executor)
        {
            _logs = logs;
            _node = node;
            _executor = executor;
        }

        public void info(string message, object? detail = null)
        {
            _logs.Add((NodeLogLevel.Info, message, detail?.ToString()));
        }

        public void warn(string message, object? detail = null)
        {
            _logs.Add((NodeLogLevel.Warning, message, detail?.ToString()));
        }

        public void error(string message, object? detail = null)
        {
            _logs.Add((NodeLogLevel.Error, message, detail?.ToString()));
        }

        public void debug(string message, object? detail = null)
        {
            _logs.Add((NodeLogLevel.Debug, message, detail?.ToString()));
        }
    }

    private class JsonApi
    {
        public object? parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var doc = JsonDocument.Parse(json);
                return ConvertJsonElement(doc.RootElement);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Recursively converts JsonElement to objects that Jint can access.
        /// </summary>
        private object? ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in element.EnumerateObject())
                    {
                        dict[prop.Name] = ConvertJsonElement(prop.Value);
                    }
                    return dict;
                    
                case JsonValueKind.Array:
                    var list = new List<object?>();
                    foreach (var item in element.EnumerateArray())
                    {
                        list.Add(ConvertJsonElement(item));
                    }
                    return list.ToArray(); // Use array for better JS compatibility
                    
                case JsonValueKind.String:
                    return element.GetString();
                    
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var l)) return l;
                    return element.GetDouble();
                    
                case JsonValueKind.True:
                    return true;
                    
                case JsonValueKind.False:
                    return false;
                    
                default:
                    return null;
            }
        }

        public string stringify(object? value)
        {
            return value != null 
                ? JsonSerializer.Serialize(value) 
                : "null";
        }
    }

    private class HttpApi
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public HttpApi(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Converts a Jint object to a string dictionary for headers.
        /// </summary>
        private Dictionary<string, string> ConvertToHeaders(object? headersObj)
        {
            var result = new Dictionary<string, string>();
            if (headersObj == null) return result;

            // Handle Jint ObjectInstance
            if (headersObj is Jint.Native.Object.ObjectInstance jintObj)
            {
                foreach (var prop in jintObj.GetOwnProperties())
                {
                    var key = prop.Key.ToString();
                    var value = prop.Value.Value?.ToString() ?? "";
                    result[key] = value;
                }
            }
            // Handle Dictionary<string, string>
            else if (headersObj is Dictionary<string, string> dict)
            {
                return dict;
            }
            // Handle ExpandoObject or other dynamic types
            else if (headersObj is System.Dynamic.ExpandoObject expando)
            {
                foreach (var kvp in (IDictionary<string, object?>)expando)
                {
                    result[kvp.Key] = kvp.Value?.ToString() ?? "";
                }
            }
            
            return result;
        }

        /// <summary>
        /// Synchronous GET request for use in Jint scripts.
        /// </summary>
        public HttpResponseResult get(string url, object? headers = null)
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            
            var headerDict = ConvertToHeaders(headers);
            foreach (var h in headerDict)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
            }

            try
            {
                var response = client.GetAsync(url).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseResult
                {
                    statusCode = (int)response.StatusCode,
                    body = body,
                    isSuccess = response.IsSuccessStatusCode
                };
            }
            catch (Exception ex)
            {
                return new HttpResponseResult
                {
                    statusCode = 0,
                    body = ex.Message,
                    isSuccess = false
                };
            }
        }

        /// <summary>
        /// Synchronous POST request for use in Jint scripts.
        /// </summary>
        public HttpResponseResult post(string url, object? body = null, object? headers = null)
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            
            var headerDict = ConvertToHeaders(headers);
            foreach (var h in headerDict)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
            }

            try
            {
                var content = body != null 
                    ? new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
                    : null;

                var response = client.PostAsync(url, content).GetAwaiter().GetResult();
                var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseResult
                {
                    statusCode = (int)response.StatusCode,
                    body = responseBody,
                    isSuccess = response.IsSuccessStatusCode
                };
            }
            catch (Exception ex)
            {
                return new HttpResponseResult
                {
                    statusCode = 0,
                    body = ex.Message,
                    isSuccess = false
                };
            }
        }

        /// <summary>
        /// Synchronous POST request with form-urlencoded body for OAuth token requests.
        /// </summary>
        public HttpResponseResult postForm(string url, object? formData = null, object? headers = null)
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            
            var headerDict = ConvertToHeaders(headers);
            foreach (var h in headerDict)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
            }

            try
            {
                var formDict = new Dictionary<string, string>();
                
                // Convert Jint object to form dictionary
                if (formData is Jint.Native.Object.ObjectInstance jintObj)
                {
                    foreach (var prop in jintObj.GetOwnProperties())
                    {
                        var key = prop.Key.ToString();
                        var value = prop.Value.Value?.ToString() ?? "";
                        formDict[key] = value;
                    }
                }
                else if (formData is Dictionary<string, object?> dict)
                {
                    foreach (var kvp in dict)
                    {
                        formDict[kvp.Key] = kvp.Value?.ToString() ?? "";
                    }
                }

                var content = new FormUrlEncodedContent(formDict);
                var response = client.PostAsync(url, content).GetAwaiter().GetResult();
                var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseResult
                {
                    statusCode = (int)response.StatusCode,
                    body = responseBody,
                    isSuccess = response.IsSuccessStatusCode
                };
            }
            catch (Exception ex)
            {
                return new HttpResponseResult
                {
                    statusCode = 0,
                    body = ex.Message,
                    isSuccess = false
                };
            }
        }
    }

    public class HttpResponseResult
    {
        public int statusCode { get; set; }
        public string body { get; set; } = "";
        public bool isSuccess { get; set; }
    }

    #endregion
}
