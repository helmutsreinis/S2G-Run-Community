using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Azure Queue Monitor trigger node - polls Azure Storage Queue for messages.
/// Reads and deletes messages, extracting JSON properties when applicable.
/// </summary>
public class AzureQueueMonitorNode : BaseNodeExecutor
{
    public AzureQueueMonitorNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "AzureQueueMonitor";

    public override List<string> GetOutputParameters() => new()
    {
        "MessageId",
        "MessageText",
        "InsertionTime",
        "ExpirationTime",
        "DequeueCount",
        "IsJson",
        "(jsonProperty)"
    };

    private void AddLog(Guid nodeId, NodeLogLevel level, string message, string? detail = null)
    {
        _executionManager.AddNodeLog(nodeId, level, message, detail);
    }

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<AzureQueueMonitorConfig>(node.Configuration ?? "{}") ?? new();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            AddLog(node.Id, NodeLogLevel.Error, "Connection string is required");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Connection string is required"
            };
        }

        if (string.IsNullOrWhiteSpace(config.QueueName))
        {
            AddLog(node.Id, NodeLogLevel.Error, "Queue name is required");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Queue name is required"
            };
        }

        // Toggle running state
        if (_executionManager.IsRunning(node.Id))
        {
            _executionManager.StopNode(node.Id);
            AddLog(node.Id, NodeLogLevel.Info, $"Stopped Azure Queue Monitor for queue: {config.QueueName}");
            node.Status = NodeStatus.Idle;
            return new NodeExecutionResult { Success = true };
        }

        // Validate connection
        try
        {
            var queueClient = new QueueClient(config.ConnectionString, config.QueueName);
            await queueClient.CreateIfNotExistsAsync();
            AddLog(node.Id, NodeLogLevel.Info, $"Connection validated for queue: {config.QueueName}");
        }
        catch (Exception ex)
        {
            AddLog(node.Id, NodeLogLevel.Error, $"Failed to connect to queue: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"Failed to connect to queue: {ex.Message}"
            };
        }

        var intervalMs = Math.Max(10, config.PollIntervalSeconds) * 1000;

        AddLog(node.Id, NodeLogLevel.Info, 
            $"Starting Azure Queue Monitor for queue: {config.QueueName} (interval: {config.PollIntervalSeconds}s, batch: {config.BatchSize})");

        // Start polling
        _executionManager.StartPollingTrigger(
            node.Id,
            intervalMs,
            async () => await PollForMessages(node.Id, config));

        node.Status = NodeStatus.Running;
        AddLog(node.Id, NodeLogLevel.Info, "Monitor started successfully. Polling is now active.");
        return new NodeExecutionResult { Success = true };
    }

    private async Task PollForMessages(Guid nodeId, AzureQueueMonitorConfig config)
    {
        var pollStartTime = DateTime.UtcNow;
        AddLog(nodeId, NodeLogLevel.Info, $"Polling queue '{config.QueueName}' for messages...");

        try
        {
            var queueClient = new QueueClient(config.ConnectionString, config.QueueName);
            
            var visibilityTimeout = TimeSpan.FromSeconds(Math.Max(1, config.VisibilityTimeoutSeconds));
            var batchSize = Math.Max(1, Math.Min(32, config.BatchSize)); // Azure limit is 32

            var messages = await queueClient.ReceiveMessagesAsync(
                maxMessages: batchSize,
                visibilityTimeout: visibilityTimeout);

            if (messages?.Value == null || messages.Value.Length == 0)
            {
                var elapsed = (DateTime.UtcNow - pollStartTime).TotalMilliseconds;
                AddLog(nodeId, NodeLogLevel.Info, $"Poll complete in {elapsed:F0}ms. No messages found.");
                return;
            }

            int processedCount = 0;

            foreach (var message in messages.Value)
            {
                processedCount++;

                // Try to decode Base64 if the message looks encoded
                var messageContent = message.MessageText ?? "";
                var isBase64Decoded = false;
                
                if (!string.IsNullOrWhiteSpace(messageContent))
                {
                    var decodedContent = TryDecodeBase64(messageContent);
                    if (decodedContent != null)
                    {
                        messageContent = decodedContent;
                        isBase64Decoded = true;
                    }
                }

                var outputData = new Dictionary<string, object?>
                {
                    ["MessageId"] = message.MessageId,
                    ["MessageText"] = messageContent, // Use decoded content
                    ["RawMessageText"] = message.MessageText, // Original (possibly encoded) content
                    ["IsBase64Decoded"] = isBase64Decoded,
                    ["InsertionTime"] = message.InsertedOn?.ToString("o"),
                    ["ExpirationTime"] = message.ExpiresOn?.ToString("o"),
                    ["DequeueCount"] = message.DequeueCount,
                    ["IsJson"] = false
                };

                // Try to parse as JSON and extract properties
                if (!string.IsNullOrWhiteSpace(messageContent))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(messageContent);
                        
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            outputData["IsJson"] = true;
                            ExtractJsonProperties(doc.RootElement, "", outputData);
                            // Store sample for dynamic placeholder detection in UI
                            outputData["_MessageSample"] = messageContent;
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            // Valid JSON array - set IsJson true and store the array
                            outputData["IsJson"] = true;
                            outputData["items"] = messageContent;
                            outputData["itemCount"] = doc.RootElement.GetArrayLength();
                            // Store sample for dynamic placeholder detection in UI
                            outputData["_MessageSample"] = messageContent;
                        }
                    }
                    catch (JsonException)
                    {
                        // Not valid JSON - that's fine, just use MessageText
                    }
                }

                AddLog(nodeId, NodeLogLevel.Info, 
                    $"🔔 Message received: {message.MessageId}", 
                    message.MessageText?.Length > 200 
                        ? message.MessageText[..200] + "..." 
                        : message.MessageText);

                // Delete the message from queue
                try
                {
                    await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt);
                    AddLog(nodeId, NodeLogLevel.Info, $"Message deleted from queue: {message.MessageId}");
                }
                catch (Exception ex)
                {
                    AddLog(nodeId, NodeLogLevel.Warning, $"Failed to delete message {message.MessageId}: {ex.Message}");
                }

                // Trigger downstream execution
                _executionManager.TriggerNodeExecution(nodeId, outputData);
            }

            var totalElapsed = (DateTime.UtcNow - pollStartTime).TotalMilliseconds;
            AddLog(nodeId, NodeLogLevel.Info, 
                $"Poll complete in {totalElapsed:F0}ms. Processed {processedCount} message(s).");
        }
        catch (Exception ex)
        {
            AddLog(nodeId, NodeLogLevel.Error, $"Error polling queue: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively extracts JSON properties and adds them to output data.
    /// </summary>
    private void ExtractJsonProperties(JsonElement element, string prefix, Dictionary<string, object?> outputData)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in element.EnumerateObject())
        {
            var fullPath = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    // Store the object as JSON string and recurse
                    outputData[fullPath] = prop.Value.GetRawText();
                    ExtractJsonProperties(prop.Value, fullPath, outputData);
                    break;
                case JsonValueKind.Array:
                    outputData[fullPath] = prop.Value.GetRawText();
                    break;
                case JsonValueKind.String:
                    outputData[fullPath] = prop.Value.GetString();
                    break;
                case JsonValueKind.Number:
                    outputData[fullPath] = prop.Value.GetDouble();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    outputData[fullPath] = prop.Value.GetBoolean();
                    break;
                case JsonValueKind.Null:
                    outputData[fullPath] = null;
                    break;
            }
        }
    }

    /// <summary>
    /// Attempts to decode a Base64-encoded string.
    /// Returns the decoded content if successful; null if not Base64 or decode fails.
    /// Uses heuristics: content must be valid Base64 chars, decode to valid UTF-8, and result should look like text.
    /// </summary>
    private string? TryDecodeBase64(string content)
    {
        // Quick checks to avoid unnecessary decode attempts
        if (string.IsNullOrWhiteSpace(content)) return null;
        
        // If it already looks like JSON, don't try to decode
        var trimmed = content.Trim();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("[")) return null;
        
        // Check if content looks like Base64 (only valid chars, reasonable length)
        if (content.Length < 4) return null; // Too short to be useful Base64
        
        // Base64 shouldn't contain newlines, spaces (except at predictable positions), or other special chars
        // except =, +, /
        foreach (var c in content)
        {
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '/' && c != '=' && !char.IsWhiteSpace(c))
                return null;
        }
        
        try
        {
            // Remove any whitespace that might have been added
            var cleanContent = content.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            
            // Pad if necessary
            var padding = cleanContent.Length % 4;
            if (padding > 0)
            {
                cleanContent = cleanContent.PadRight(cleanContent.Length + (4 - padding), '=');
            }
            
            var bytes = Convert.FromBase64String(cleanContent);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            
            // Verify the decode produced valid text (not binary garbage)
            // Check if it contains lots of control characters which would indicate binary data
            var controlCharCount = decoded.Count(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t');
            if (controlCharCount > decoded.Length * 0.1) // More than 10% control chars = probably binary
                return null;
            
            // Success - return the decoded content
            return decoded;
        }
        catch
        {
            // Not valid Base64
            return null;
        }
    }
}

public class AzureQueueMonitorConfig
{
    public string? ConnectionString { get; set; }
    public string? QueueName { get; set; }
    public int PollIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 1;
    public int VisibilityTimeoutSeconds { get; set; } = 30;
}
