using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Loop node that iterates over a JSON array and triggers downstream nodes for each item.
/// Supports optional parallel batch processing.
/// </summary>
public class LoopNode : BaseNodeExecutor
{
    public LoopNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Loop";

    public override List<string> GetOutputParameters() => new()
    {
        "CurrentItem", "CurrentIndex", "TotalCount", "IsFirstItem", "IsLastItem",
        "BatchNumber", "ProcessedCount", "ErrorCount"
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<LoopConfig>(node.Configuration ?? "{}") ?? new();

        // Resolve placeholders in input array
        var inputArrayJson = ResolvePlaceholders(config.InputArray ?? "", inputData);

        if (string.IsNullOrWhiteSpace(inputArrayJson))
        {
            Log(node, NodeLogLevel.Error, "Input array is empty or not provided");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Input array is empty or not provided"
            };
        }

        // Parse the JSON array - extract raw strings to avoid JsonDocument disposal issues
        List<string> itemJsonStrings;
        string? firstItemSample = null;
        List<string>? detectedNestedArrays = null;
        
        try
        {
            using var doc = JsonDocument.Parse(inputArrayJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Log(node, NodeLogLevel.Error, "Input is not a JSON array", $"Received: {doc.RootElement.ValueKind}");
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Input is not a JSON array"
                };
            }

            // Clone raw JSON strings BEFORE document is disposed
            itemJsonStrings = doc.RootElement.EnumerateArray()
                .Select(e => e.GetRawText())
                .ToList();
            
            // Capture first item sample and detect nested arrays while document is still valid
            if (doc.RootElement.GetArrayLength() > 0)
            {
                var firstElement = doc.RootElement[0];
                firstItemSample = firstElement.GetRawText();
                detectedNestedArrays = DetectNestedArrays(firstElement);
            }
        }
        catch (JsonException ex)
        {
            Log(node, NodeLogLevel.Error, "Failed to parse JSON array", ex.Message);
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse JSON array: {ex.Message}"
            };
        }

        var totalCount = itemJsonStrings.Count;
        var batchSize = Math.Max(1, config.BatchSize);
        var delayMs = Math.Max(0, config.DelayBetweenBatches);

        Log(node, NodeLogLevel.Info, $"Processing {totalCount} items", $"Batch size: {batchSize}, Delay: {delayMs}ms");

        if (totalCount == 0)
        {
            Log(node, NodeLogLevel.Warning, "Input array is empty, no items to process");
            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "CurrentItem", null },
                    { "CurrentIndex", -1 },
                    { "TotalCount", 0 },
                    { "IsFirstItem", false },
                    { "IsLastItem", false },
                    { "BatchNumber", 0 },
                    { "ProcessedCount", 0 },
                    { "ErrorCount", 0 }
                }
            };
        }

        int processedCount = 0;
        int errorCount = 0;
        int batchNumber = 0;
        Dictionary<string, object?> lastOutputData = new();

        // Process items in batches
        for (int i = 0; i < totalCount; i += batchSize)
        {
            batchNumber++;
            var batchItems = itemJsonStrings.Skip(i).Take(batchSize).ToList();
            var batchTasks = new List<Task>();

            Log(node, NodeLogLevel.Info, $"Processing batch {batchNumber}", $"Items {i + 1} to {Math.Min(i + batchSize, totalCount)} of {totalCount}");

            if (batchSize == 1)
            {
                // Sequential processing
                foreach (var (itemJson, batchIndex) in batchItems.Select((item, idx) => (item, idx)))
                {
                    var globalIndex = i + batchIndex;
                    var outputData = CreateItemOutputData(itemJson, globalIndex, totalCount, batchNumber);
                    lastOutputData = outputData;
                    processedCount++;

                    Log(node, NodeLogLevel.Info, $"Processing item {globalIndex + 1}/{totalCount}",
                        itemJson.Length > 200 ? itemJson[..200] + "..." : itemJson);

                    // Signal that this iteration's data is ready - downstream will pick it up
                    _executionManager?.TriggerNodeExecution(node.Id, outputData);
                }
            }
            else
            {
                // Parallel batch processing - prepare all items in batch
                var batchOutputs = batchItems.Select((itemJson, batchIndex) =>
                {
                    var globalIndex = i + batchIndex;
                    return CreateItemOutputData(itemJson, globalIndex, totalCount, batchNumber);
                }).ToList();

                // Process batch items in parallel
                var parallelTasks = batchOutputs.Select(async outputData =>
                {
                    Interlocked.Increment(ref processedCount);

                    var currentIndex = outputData.TryGetValue("CurrentIndex", out var idx) ? idx : 0;
                    Log(node, NodeLogLevel.Info, $"Processing item {(int)currentIndex! + 1}/{totalCount} (parallel)");

                    _executionManager?.TriggerNodeExecution(node.Id, outputData);
                    await Task.CompletedTask;
                });

                await Task.WhenAll(parallelTasks);
                lastOutputData = batchOutputs.LastOrDefault() ?? new Dictionary<string, object?>();
            }

            // Apply delay between batches (not after the last batch)
            if (delayMs > 0 && i + batchSize < totalCount)
            {
                Log(node, NodeLogLevel.Info, $"Waiting {delayMs}ms before next batch");
                await Task.Delay(delayMs);
            }
        }
        // Update statistics and sample for placeholder detection
        config.LastExecutedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        config.LastItemCount = totalCount;
        
        // Store sample of first element for design-time placeholder generation (captured earlier before doc disposal)
        if (!string.IsNullOrEmpty(firstItemSample))
        {
            // Truncate to max 5KB to avoid storing huge payloads
            var sampleJson = firstItemSample.Length > 5120 
                ? firstItemSample.Substring(0, 5120) 
                : firstItemSample;
            config.LastInputArraySample = sampleJson;
            config.DetectedNestedArrays = detectedNestedArrays;
            lastOutputData["_LoopSample"] = sampleJson;
        }
        
        node.Configuration = JsonSerializer.Serialize(config);

        Log(node, NodeLogLevel.Info, $"Loop completed", $"Processed: {processedCount}, Errors: {errorCount}");

        lastOutputData["ProcessedCount"] = processedCount;
        lastOutputData["ErrorCount"] = errorCount;

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = lastOutputData
        };
    }

    private Dictionary<string, object?> CreateItemOutputData(string itemJson, int index, int totalCount, int batchNumber)
    {
        var outputData = new Dictionary<string, object?>
        {
            { "CurrentItem", itemJson },
            { "CurrentIndex", index },
            { "TotalCount", totalCount },
            { "IsFirstItem", index == 0 },
            { "IsLastItem", index == totalCount - 1 },
            { "BatchNumber", batchNumber }
        };

        // Parse and extract top-level properties from the item for downstream placeholder access
        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            var item = doc.RootElement;
            
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in item.EnumerateObject())
                {
                    var value = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                    outputData[prop.Name] = value;
                }
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                outputData["Value"] = item.GetString();
            }
            else if (item.ValueKind == JsonValueKind.Number)
            {
                outputData["Value"] = item.TryGetInt64(out var l) ? l : item.GetDouble();
            }
        }
        catch
        {
            // If parsing fails, just use the raw item as CurrentItem (already set above)
        }

        return outputData;
    }

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var result = template;

        // Handle {{placeholder}} format
        var placeholderRegex = new Regex(@"\{\{([^}]+)\}\}");
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
    /// Detects nested array properties within a JSON element for warning display.
    /// </summary>
    private List<string> DetectNestedArrays(JsonElement element)
    {
        var nestedArrays = new List<string>();
        
        if (element.ValueKind != JsonValueKind.Object)
            return nestedArrays;
            
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                nestedArrays.Add(property.Name);
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                // Check one level deep for nested arrays
                foreach (var nestedProp in property.Value.EnumerateObject())
                {
                    if (nestedProp.Value.ValueKind == JsonValueKind.Array)
                    {
                        nestedArrays.Add($"{property.Name}.{nestedProp.Name}");
                    }
                }
            }
        }
        
        return nestedArrays;
    }
}

public class LoopConfig
{
    public string? InputArray { get; set; }
    public int BatchSize { get; set; } = 1;
    public int DelayBetweenBatches { get; set; } = 0;

    // Statistics (persisted in configuration)
    public string? LastExecutedAt { get; set; }
    public int? LastItemCount { get; set; }
    public string? LastExecutionSample { get; set; }
    
    // Sample of first array element for placeholder detection
    public string? LastInputArraySample { get; set; }
    
    // Detected nested arrays for warning display
    public List<string>? DetectedNestedArrays { get; set; }
}
