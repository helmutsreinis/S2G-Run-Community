using System.Text.Json;
using Azure.Storage.Queues;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Sends messages to Azure Storage Queue.
/// </summary>
public class AzureQueueSendNode : BaseNodeExecutor
{
    public AzureQueueSendNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "AzureQueueSend";

    public override List<string> GetOutputParameters() => new() 
    { 
        "MessageId", 
        "InsertionTime", 
        "ExpirationTime", 
        "Success" 
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<AzureQueueSendConfig>(node.Configuration ?? "{}") ?? new();

        // Resolve placeholders
        var connectionString = ResolvePlaceholders(config.ConnectionString ?? "", inputData);
        var queueName = ResolvePlaceholders(config.QueueName ?? "", inputData);
        var message = ResolvePlaceholders(config.Message ?? "", inputData);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Connection string is required"
            };
        }

        if (string.IsNullOrWhiteSpace(queueName))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Queue name is required"
            };
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Message content is required"
            };
        }

        try
        {
            var queueClient = new QueueClient(connectionString, queueName);
            
            // Ensure queue exists
            await queueClient.CreateIfNotExistsAsync();

            // Determine TTL
            TimeSpan? timeToLive = null;
            if (config.TimeToLiveSeconds.HasValue && config.TimeToLiveSeconds.Value > 0)
            {
                timeToLive = TimeSpan.FromSeconds(config.TimeToLiveSeconds.Value);
            }

            // Send message
            var receipt = await queueClient.SendMessageAsync(message, timeToLive: timeToLive);

            Log(node, NodeLogLevel.Info, $"Message sent to queue '{queueName}'", 
                $"MessageId: {receipt.Value.MessageId}");

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    ["MessageId"] = receipt.Value.MessageId,
                    ["InsertionTime"] = receipt.Value.InsertionTime.ToString("o"),
                    ["ExpirationTime"] = receipt.Value.ExpirationTime.ToString("o"),
                    ["Success"] = true
                }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Failed to send message to queue: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                OutputData = new Dictionary<string, object?>
                {
                    ["Success"] = false
                }
            };
        }
    }

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        
        var result = template;
        var placeholderRegex = new System.Text.RegularExpressions.Regex(@"\{\{([^}]+)\}\}");
        
        result = placeholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            
            if (data.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? "";
            
            var shortKey = key.Contains('.') ? key.Split('.').Last() : key;
            if (data.TryGetValue(shortKey, out var shortValue) && shortValue != null)
                return shortValue.ToString() ?? "";
            
            foreach (var kvp in data)
            {
                if (kvp.Key.EndsWith("." + key) || kvp.Key.EndsWith("." + shortKey))
                    return kvp.Value?.ToString() ?? "";
            }
            
            return match.Value;
        });
        
        return result;
    }
}

public class AzureQueueSendConfig
{
    public string? ConnectionString { get; set; }
    public string? QueueName { get; set; }
    public string? Message { get; set; }
    public int? TimeToLiveSeconds { get; set; }
}
