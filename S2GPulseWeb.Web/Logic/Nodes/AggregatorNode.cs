using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Aggregator node that collects JSON items and triggers downstream only when threshold is reached.
/// Supports schema validation with routing of invalid items to separate connection path.
/// </summary>
public class AggregatorNode : BaseNodeExecutor
{
    // In-memory buffer storage: WorkflowId -> NodeId -> List of buffered items
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, List<string>>> _buffers = new();

    public AggregatorNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Aggregator";

    public override List<string> GetOutputParameters() => new()
    {
        "AggregatedItems", "AggregatedItemsJson", "ItemCount", "BufferSize",
        "InvalidItem", "InvalidReason", "IsThresholdReached"
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        await Task.CompletedTask; // Synchronous execution - satisfies async signature
        var config = JsonSerializer.Deserialize<AggregatorConfig>(node.Configuration ?? "{}") ?? new();

        // Handle incoming item - resolve placeholders
        var incomingItemJson = ResolvePlaceholders(config.InputItem ?? "", inputData);

        // Resolve threshold count - support placeholder or literal number
        var thresholdStr = ResolvePlaceholders(config.ThresholdCount ?? "10", inputData);
        if (!int.TryParse(thresholdStr, out var thresholdCount) || thresholdCount <= 0)
        {
            thresholdCount = 10; // Default fallback
            Log(node, NodeLogLevel.Warning, $"Invalid threshold '{thresholdStr}', using default: 10");
        }

        // Get or create buffer for this workflow+node combination
        var workflowId = node.WorkflowId;
        var nodeBuffers = _buffers.GetOrAdd(workflowId, _ => new ConcurrentDictionary<Guid, List<string>>());
        var buffer = nodeBuffers.GetOrAdd(node.Id, _ => new List<string>());

        // If no incoming item, just return current buffer status
        if (string.IsNullOrWhiteSpace(incomingItemJson))
        {
            Log(node, NodeLogLevel.Info, $"No input item received. Buffer size: {buffer.Count}/{thresholdCount}");
            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AggregatedItems", null },
                    { "AggregatedItemsJson", "[]" },
                    { "ItemCount", 0 },
                    { "BufferSize", buffer.Count },
                    { "InvalidItem", null },
                    { "InvalidReason", null },
                    { "IsThresholdReached", false }
                }
            };
        }

        // Validate item against schema if schema is defined
        if (!string.IsNullOrEmpty(config.SchemaJson))
        {
            var (isValid, reason) = ValidateAgainstSchema(incomingItemJson, config.SchemaJson);
            if (!isValid)
            {
                Log(node, NodeLogLevel.Warning, $"Item failed schema validation: {reason}",
                    incomingItemJson.Length > 500 ? incomingItemJson[..500] + "..." : incomingItemJson);

                // Optionally keep invalid item in buffer
                if (config.KeepInvalidItems)
                {
                    lock (buffer)
                    {
                        buffer.Add(incomingItemJson);
                    }
                }

                // Return invalid item for routing to "invalid" connection
                return new NodeExecutionResult
                {
                    Success = true,
                    OutputData = new Dictionary<string, object?>
                    {
                        { "AggregatedItems", null },
                        { "AggregatedItemsJson", "[]" },
                        { "ItemCount", 0 },
                        { "BufferSize", buffer.Count },
                        { "InvalidItem", incomingItemJson },
                        { "InvalidReason", reason },
                        { "IsThresholdReached", false },
                        { "_ValidationFailed", true } // Internal flag for routing
                    }
                };
            }
        }

        // Atomic add + threshold check + clear inside a single lock
        // This prevents the race where two threads both see threshold met
        // and one gets a full batch while the other gets an empty array
        List<string>? aggregatedItems = null;
        int currentBufferSize;
        lock (buffer)
        {
            buffer.Add(incomingItemJson);
            currentBufferSize = buffer.Count;

            if (currentBufferSize >= thresholdCount)
            {
                aggregatedItems = new List<string>(buffer);
                buffer.Clear();
            }
        }

        Log(node, NodeLogLevel.Info, $"Item added to buffer. Buffer size: {currentBufferSize}/{thresholdCount}");

        if (aggregatedItems != null)
        {
            // Build JSON array using StringBuilder to reduce memory pressure for large batches
            var sb = new StringBuilder(aggregatedItems.Sum(s => s.Length) + aggregatedItems.Count + 2);
            sb.Append('[');
            for (int i = 0; i < aggregatedItems.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(aggregatedItems[i]);
            }
            sb.Append(']');
            var aggregatedJson = sb.ToString();

            Log(node, NodeLogLevel.Info, $"Threshold reached! Emitting {aggregatedItems.Count} items.",
                aggregatedJson.Length > 1000 ? aggregatedJson[..1000] + "..." : aggregatedJson);

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AggregatedItems", aggregatedJson },
                    { "AggregatedItemsJson", aggregatedJson },
                    { "ItemCount", aggregatedItems.Count },
                    { "BufferSize", 0 },
                    { "InvalidItem", null },
                    { "InvalidReason", null },
                    { "IsThresholdReached", true }
                }
            };
        }

        // Threshold not yet reached - return success but IsThresholdReached=false
        // WorkflowExecutionService will block downstream via connection filtering
        return new NodeExecutionResult
        {
            Success = true, // Node executed successfully, just waiting for more items
            OutputData = new Dictionary<string, object?>
            {
                { "AggregatedItems", null },
                { "AggregatedItemsJson", "[]" },
                { "ItemCount", 0 },
                { "BufferSize", currentBufferSize },
                { "InvalidItem", null },
                { "InvalidReason", null },
                { "IsThresholdReached", false }
            }
        };
    }

    /// <summary>
    /// Validates an item against the detected schema, supporting nested dot-notation property paths.
    /// </summary>
    private (bool IsValid, string? Reason) ValidateAgainstSchema(string itemJson, string schemaJson)
    {
        try
        {
            var schema = JsonSerializer.Deserialize<List<SchemaProperty>>(schemaJson);
            if (schema == null || !schema.Any())
                return (true, null); // No schema = everything passes

            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (false, "Item must be a JSON object");

            foreach (var prop in schema)
            {
                var (found, element) = NavigateToProperty(root, prop.Name);
                
                if (prop.Required && !found)
                {
                    return (false, $"Missing required property: {prop.Name}");
                }

                if (found)
                {
                    var actualType = GetJsonType(element.ValueKind);
                    if (!string.IsNullOrEmpty(prop.Type) && actualType != prop.Type && actualType != "null")
                    {
                        return (false, $"Property '{prop.Name}' expected type '{prop.Type}' but got '{actualType}'");
                    }
                }
            }

            return (true, null);
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Navigates to a property using dot-notation path (e.g., "companyProfile.tenantId").
    /// </summary>
    private (bool Found, JsonElement Element) NavigateToProperty(JsonElement root, string path)
    {
        var parts = path.Split('.');
        var current = root;

        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object)
                return (false, default);
            
            if (!current.TryGetProperty(part, out var next))
                return (false, default);
            
            current = next;
        }

        return (true, current);
    }

    private string GetJsonType(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "unknown"
    };

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var result = template;
        var placeholderRegex = new System.Text.RegularExpressions.Regex(@"\{\{([^}]+)\}\}");

        result = placeholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;

            // Try exact match first
            if (data.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? "";

            // Try without node prefix
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
    /// Clears the buffer for a specific node (called when workflow stops).
    /// </summary>
    public static void ClearBuffer(Guid workflowId, Guid nodeId)
    {
        if (_buffers.TryGetValue(workflowId, out var nodeBuffers))
        {
            nodeBuffers.TryRemove(nodeId, out _);
        }
    }

    /// <summary>
    /// Clears all buffers for a workflow (called when workflow stops).
    /// </summary>
    public static void ClearWorkflowBuffers(Guid workflowId)
    {
        _buffers.TryRemove(workflowId, out _);
    }

    /// <summary>
    /// Gets the current buffer size for display in the UI.
    /// </summary>
    public static int GetBufferSize(Guid workflowId, Guid nodeId)
    {
        if (_buffers.TryGetValue(workflowId, out var nodeBuffers) &&
            nodeBuffers.TryGetValue(nodeId, out var buffer))
        {
            return buffer.Count;
        }
        return 0;
    }

    /// <summary>
    /// Detects schema from a sample JSON array, including nested properties with dot-notation paths.
    /// </summary>
    public static List<SchemaProperty> DetectSchemaFromSample(string sampleJson)
    {
        var schema = new List<SchemaProperty>();

        try
        {
            using var doc = JsonDocument.Parse(sampleJson);
            var root = doc.RootElement;

            JsonElement firstItem;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                firstItem = root[0];
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                firstItem = root;
            }
            else
            {
                return schema;
            }

            if (firstItem.ValueKind == JsonValueKind.Object)
            {
                DetectPropertiesRecursive(firstItem, "", schema);
            }
        }
        catch
        {
            // Invalid JSON - return empty schema
        }

        return schema;
    }

    /// <summary>
    /// Recursively detects properties from a JSON element, building dot-notation paths for nested properties.
    /// </summary>
    private static void DetectPropertiesRecursive(JsonElement element, string prefix, List<SchemaProperty> schema)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in element.EnumerateObject())
        {
            var fullPath = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            var valueType = GetJsonTypeStatic(prop.Value.ValueKind);

            schema.Add(new SchemaProperty
            {
                Name = fullPath,
                Type = valueType,
                Required = true // Default to required
            });

            // Recursively process nested objects (but not arrays)
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                DetectPropertiesRecursive(prop.Value, fullPath, schema);
            }
        }
    }

    private static string GetJsonTypeStatic(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "unknown"
    };
}

public class AggregatorConfig
{
    public string? InputItem { get; set; }
    public string ThresholdCount { get; set; } = "10";
    public string? SchemaJson { get; set; }
    public bool KeepInvalidItems { get; set; } = false;
    
    // Sample used for schema detection (design-time only)
    public string? SampleJson { get; set; }
}

public class SchemaProperty
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; } = true;
}
