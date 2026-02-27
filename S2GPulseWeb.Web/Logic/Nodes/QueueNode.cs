using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Queue node executor - stores data and forwards to downstream nodes with optional delay
/// </summary>
public class QueueNode : BaseNodeExecutor
{
    private static readonly ConcurrentDictionary<string, Queue<Dictionary<string, object>>> _nodeQueues = new();
    
    public QueueNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Queue";

    public override List<string> GetOutputParameters() => new() 
    { 
        "QueueOutput", "QueueSize", "TotalEnqueued", "TotalProcessed", 
        "TotalRejected", "TotalExpired", "Statistics" 
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<QueueConfig>(node.Configuration ?? "{}") ?? new();
        
        var queueName = !string.IsNullOrWhiteSpace(config.QueueName) ? config.QueueName : "default";
        var maxSize = config.MaxSize > 0 ? config.MaxSize : 0;
        var expirationMinutes = config.ExpirationMinutes > 0 ? config.ExpirationMinutes : 0;
        var delayMs = config.DelayMs > 0 ? config.DelayMs : 0;

        // Initialize queue if not exists
        var queue = _nodeQueues.GetOrAdd(queueName, _ => new Queue<Dictionary<string, object>>());

        // Remove expired items if expiration is configured
        if (expirationMinutes > 0)
        {
            RemoveExpiredItems(queue, expirationMinutes, node, config);
        }

        // Capture incoming data from upstream nodes
        var incomingData = new Dictionary<string, object>();
        bool hasIncomingData = false;

        if (!string.IsNullOrWhiteSpace(config.InputProperties))
        {
            // User specified which properties to capture - parse the comma-separated list
            var propertiesToCapture = config.InputProperties.Split(',')
                .Select(p => p.Trim())
                .Select(p => p.StartsWith("{") && p.EndsWith("}") ? p[1..^1] : p)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            foreach (var key in propertiesToCapture)
            {
                // Check for both raw key and any prefixed versions
                foreach (var inputKey in inputData.Keys)
                {
                    // Match exact key or key at end of prefixed format (e.g., "HttpListener.Body" matches "Body")
                    if (inputKey == key || inputKey.EndsWith("." + key))
                    {
                        if (inputData[inputKey] != null)
                        {
                            // Store with the raw key name for simpler downstream access
                            incomingData[key] = inputData[inputKey]!;
                            hasIncomingData = true;
                        }
                    }
                }
            }

            if (!hasIncomingData)
            {
                Log(node, NodeLogLevel.Warning, "Input properties not found",
                    $"Configured properties '{config.InputProperties}' not found in upstream node data. Available: {string.Join(", ", inputData.Keys)}");
            }
        }
        else
        {
            // No specific properties specified - capture ALL incoming data
            foreach (var kvp in inputData)
            {
                if (kvp.Value != null)
                {
                    // For prefixed keys like "HttpListener.Body", extract both full key and raw key
                    incomingData[kvp.Key] = kvp.Value;
                    hasIncomingData = true;
                    
                    // Also add the short key if it's a prefixed format
                    if (kvp.Key.Contains('.'))
                    {
                        var shortKey = kvp.Key.Split('.').Last();
                        if (!incomingData.ContainsKey(shortKey))
                        {
                            incomingData[shortKey] = kvp.Value;
                        }
                    }
                }
            }
        }

        // Initialize statistics in config
        config.TotalEnqueued ??= 0;
        config.TotalProcessed ??= 0;
        config.TotalRejected ??= 0;
        config.TotalExpired ??= 0;

        // Enqueue data from previous node
        if (hasIncomingData && incomingData.Count > 0)
        {
            // Check max size constraint
            if (maxSize > 0 && queue.Count >= maxSize)
            {
                config.TotalRejected++;
                config.LastError = $"Queue '{queueName}' is full (max size: {maxSize})";
                Log(node, NodeLogLevel.Warning, "Queue full - message rejected", $"Queue size: {queue.Count}/{maxSize}");

                // Update configuration and return
                node.Configuration = JsonSerializer.Serialize(config);
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = config.LastError,
                    OutputData = CreateOutputData(config, queue.Count)
                };
            }

            // Add timestamp and index to the data
            incomingData["_QueueTimestamp"] = DateTime.UtcNow;
            incomingData["_QueueIndex"] = config.TotalEnqueued + 1;

            queue.Enqueue(incomingData);
            config.TotalEnqueued++;
            config.LastEnqueueTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            // Log with data preview
            var dataPreview = incomingData.Count <= 3
                ? string.Join(", ", incomingData.Where(kv => !kv.Key.StartsWith("_")).Select(kv => kv.Key))
                : $"{incomingData.Count - 2} properties";
            Log(node, NodeLogLevel.Info, "Message enqueued", $"Queue size: {queue.Count}, Data: {dataPreview}");
        }

        // Apply delay if configured (before forwarding to downstream)
        if (delayMs > 0 && hasIncomingData)
        {
            Log(node, NodeLogLevel.Info, "Applying delay", $"Waiting {delayMs}ms before forwarding");
            await Task.Delay(delayMs);
        }

        var outputData = new Dictionary<string, object?>();

        // Immediately forward the item to downstream nodes
        if (hasIncomingData && incomingData.Count > 0)
        {
            config.LastProcessTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            // Copy item's properties to output so downstream nodes can access them
            foreach (var kvp in incomingData)
            {
                if (!kvp.Key.StartsWith("_Queue"))
                {
                    outputData[kvp.Key] = kvp.Value;
                }
            }

            // Set QueueOutput and Data properties for downstream nodes
            var jsonData = JsonSerializer.Serialize(incomingData.Where(kv => !kv.Key.StartsWith("_Queue")).ToDictionary(kv => kv.Key, kv => kv.Value));
            outputData["QueueOutput"] = jsonData;
            outputData["Data"] = jsonData;
            config.TotalProcessed++;

            Log(node, NodeLogLevel.Info, "Item forwarded", $"Data keys: {string.Join(", ", incomingData.Keys.Where(k => !k.StartsWith("_Queue")))}");
        }

        // Update statistics
        config.Statistics = $"Enqueued: {config.TotalEnqueued}, Processed: {config.TotalProcessed}, Rejected: {config.TotalRejected}, Expired: {config.TotalExpired}, Current: {queue.Count}";
        
        // Merge standard output data
        foreach (var kvp in CreateOutputData(config, queue.Count))
        {
            if (!outputData.ContainsKey(kvp.Key))
            {
                outputData[kvp.Key] = kvp.Value;
            }
        }

        // Save updated config
        node.Configuration = JsonSerializer.Serialize(config);

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = outputData
        };
    }

    private static Dictionary<string, object?> CreateOutputData(QueueConfig config, int queueSize)
    {
        return new Dictionary<string, object?>
        {
            { "QueueSize", queueSize },
            { "TotalEnqueued", config.TotalEnqueued ?? 0 },
            { "TotalProcessed", config.TotalProcessed ?? 0 },
            { "TotalRejected", config.TotalRejected ?? 0 },
            { "TotalExpired", config.TotalExpired ?? 0 },
            { "Statistics", config.Statistics ?? "" }
        };
    }

    private void RemoveExpiredItems(Queue<Dictionary<string, object>> queue, int expirationMinutes, WorkflowNode node, QueueConfig config)
    {
        var now = DateTime.UtcNow;
        var itemsToKeep = new List<Dictionary<string, object>>();
        int expiredCount = 0;

        lock (queue)
        {
            // Process all items in the queue
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();

                // Check if item has a timestamp
                if (item.TryGetValue("_QueueTimestamp", out var timestampObj) && timestampObj is DateTime timestamp)
                {
                    var age = now - timestamp;

                    if (age.TotalMinutes <= expirationMinutes)
                    {
                        itemsToKeep.Add(item);
                    }
                    else
                    {
                        expiredCount++;
                    }
                }
                else if (item.TryGetValue("_QueueTimestamp", out var tsObj) && DateTime.TryParse(tsObj?.ToString(), out var parsedTimestamp))
                {
                    var age = now - parsedTimestamp;

                    if (age.TotalMinutes <= expirationMinutes)
                    {
                        itemsToKeep.Add(item);
                    }
                    else
                    {
                        expiredCount++;
                    }
                }
                else
                {
                    // No timestamp, keep the item
                    itemsToKeep.Add(item);
                }
            }

            // Re-enqueue valid items
            foreach (var item in itemsToKeep)
            {
                queue.Enqueue(item);
            }
        }

        // Update statistics if items expired
        if (expiredCount > 0)
        {
            config.TotalExpired = (config.TotalExpired ?? 0) + expiredCount;
            Log(node, NodeLogLevel.Warning, "Items expired", $"{expiredCount} item(s) removed from queue (older than {expirationMinutes} minutes)");
        }
    }

    /// <summary>
    /// Clear all queues (static utility method)
    /// </summary>
    public static void ClearAllQueues()
    {
        _nodeQueues.Clear();
    }

    /// <summary>
    /// Clear a specific queue by name
    /// </summary>
    public static void ClearQueue(string queueName)
    {
        if (_nodeQueues.TryGetValue(queueName, out var queue))
        {
            lock (queue)
            {
                queue.Clear();
            }
        }
    }

    /// <summary>
    /// Get queue size
    /// </summary>
    public static int GetQueueSize(string queueName)
    {
        return _nodeQueues.TryGetValue(queueName, out var queue) ? queue.Count : 0;
    }

    /// <summary>
    /// Get all items currently in the queue (returns a copy for safe enumeration)
    /// </summary>
    public static List<Dictionary<string, object>> GetQueueItems(string queueName)
    {
        if (_nodeQueues.TryGetValue(queueName, out var queue))
        {
            lock (queue)
            {
                return queue.ToList();
            }
        }
        return new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// Get all queue names that have been created
    /// </summary>
    public static IEnumerable<string> GetAllQueueNames()
    {
        return _nodeQueues.Keys.ToList();
    }
}

public class QueueConfig
{
    public string? QueueName { get; set; } = "default";
    public int MaxSize { get; set; } = 0;
    public int ExpirationMinutes { get; set; } = 0;
    public int DelayMs { get; set; } = 0;
    public string? InputProperties { get; set; }
    
    // Statistics (persisted in configuration)
    public int? TotalEnqueued { get; set; } = 0;
    public int? TotalProcessed { get; set; } = 0;
    public int? TotalRejected { get; set; } = 0;
    public int? TotalExpired { get; set; } = 0;
    public string? LastEnqueueTime { get; set; }
    public string? LastProcessTime { get; set; }
    public string? LastError { get; set; }
    public string? Statistics { get; set; }
}
