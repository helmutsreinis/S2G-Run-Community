using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Cache node for storing and retrieving data with optional expiration.
/// Supports operations: Set, SetObject, Get, Delete, Clear
/// </summary>
public class CacheNode : BaseNodeExecutor
{
    private readonly CacheStorageService _cacheService;
    private Guid _workflowId;

    public CacheNode(NodeExecutionManager executionManager, CacheStorageService cacheService) 
        : base(executionManager)
    {
        _cacheService = cacheService;
    }

    public override string NodeType => "Cache";

    public override List<string> GetOutputParameters() => new() 
    { 
        "CacheValue",      // Retrieved value for Get operation
        "CacheKeys",       // List of all property names
        "CacheData",       // Entire cache as JSON
        "OperationResult"  // Success/failure message
    };

    /// <summary>
    /// Set the workflow ID for cache scoping (called before execution)
    /// </summary>
    public void SetWorkflowId(Guid workflowId)
    {
        _workflowId = workflowId;
    }

    protected override Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node, 
        Dictionary<string, object?> inputData, 
        string userId)
    {
        var config = JsonSerializer.Deserialize<CacheConfig>(node.Configuration ?? "{}") ?? new();
        var operation = config.Operation ?? "Get";
        
        // Resolve placeholders in property name and value
        var propertyName = ResolvePlaceholders(config.PropertyName ?? "", inputData);
        var value = ResolvePlaceholders(config.Value ?? "", inputData);
        var expirationMinutes = config.EnableExpiration ? config.ExpirationMinutes : (int?)null;

        var outputData = new Dictionary<string, object?>();
        string resultMessage;

        try
        {
            switch (operation.ToLower())
            {
                case "set":
                    if (string.IsNullOrEmpty(propertyName))
                    {
                        return Task.FromResult(new NodeExecutionResult
                        {
                            Success = false,
                            ErrorMessage = "Property name is required for Set operation"
                        });
                    }
                    
                    // Try to parse value as JSON, otherwise store as string
                    object? parsedValue = TryParseJsonValue(value);
                    _cacheService.Set(_workflowId, node.Id, propertyName, parsedValue, expirationMinutes);
                    
                    resultMessage = $"Set '{propertyName}' = '{value}'" + 
                        (expirationMinutes.HasValue ? $" (expires in {expirationMinutes} min)" : "");
                    Log(node, NodeLogLevel.Info, resultMessage);
                    outputData["OperationResult"] = "Success";
                    break;

                case "setobject":
                    // Parse value as JSON object and store all properties
                    try
                    {
                        var jsonObj = JsonSerializer.Deserialize<Dictionary<string, object?>>(value ?? "{}");
                        if (jsonObj != null)
                        {
                            _cacheService.SetObject(_workflowId, node.Id, jsonObj, expirationMinutes);
                            resultMessage = $"Stored {jsonObj.Count} properties from object" +
                                (expirationMinutes.HasValue ? $" (expires in {expirationMinutes} min)" : "");
                            Log(node, NodeLogLevel.Info, resultMessage);
                        }
                        outputData["OperationResult"] = "Success";
                    }
                    catch (JsonException)
                    {
                        return Task.FromResult(new NodeExecutionResult
                        {
                            Success = false,
                            ErrorMessage = "Value must be a valid JSON object for SetObject operation"
                        });
                    }
                    break;

                case "get":
                    if (string.IsNullOrEmpty(propertyName))
                    {
                        // Get all cached data
                        var allData = _cacheService.GetAll(_workflowId, node.Id);
                        outputData["CacheValue"] = null;
                        outputData["CacheData"] = JsonSerializer.Serialize(allData);
                        outputData["CacheKeys"] = string.Join(",", allData.Keys);
                        Log(node, NodeLogLevel.Info, $"Retrieved all cached data ({allData.Count} properties)", 
                            JsonSerializer.Serialize(allData, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    else
                    {
                        // Get specific property
                        var cachedValue = _cacheService.Get(_workflowId, node.Id, propertyName);
                        outputData["CacheValue"] = cachedValue;
                        outputData["CacheKeys"] = string.Join(",", _cacheService.GetAllKeys(_workflowId, node.Id));
                        
                        if (cachedValue != null)
                        {
                            Log(node, NodeLogLevel.Info, $"Retrieved '{propertyName}' = '{cachedValue}'");
                        }
                        else
                        {
                            Log(node, NodeLogLevel.Warning, $"Property '{propertyName}' not found or expired");
                        }
                    }
                    outputData["OperationResult"] = "Success";
                    break;

                case "delete":
                    if (string.IsNullOrEmpty(propertyName))
                    {
                        return Task.FromResult(new NodeExecutionResult
                        {
                            Success = false,
                            ErrorMessage = "Property name is required for Delete operation"
                        });
                    }
                    
                    var deleted = _cacheService.Delete(_workflowId, node.Id, propertyName);
                    resultMessage = deleted 
                        ? $"Deleted property '{propertyName}'" 
                        : $"Property '{propertyName}' not found";
                    Log(node, deleted ? NodeLogLevel.Info : NodeLogLevel.Warning, resultMessage);
                    outputData["OperationResult"] = deleted ? "Deleted" : "NotFound";
                    break;

                case "clear":
                    _cacheService.Clear(_workflowId, node.Id);
                    Log(node, NodeLogLevel.Info, "Cleared all cached data");
                    outputData["OperationResult"] = "Cleared";
                    outputData["CacheValue"] = null;
                    outputData["CacheKeys"] = "";
                    outputData["CacheData"] = "{}";
                    break;

                default:
                    return Task.FromResult(new NodeExecutionResult
                    {
                        Success = false,
                        ErrorMessage = $"Unknown operation: {operation}"
                    });
            }

            // Always include current cache state in output for placeholder access
            var currentData = _cacheService.GetAll(_workflowId, node.Id);
            foreach (var kvp in currentData)
            {
                outputData[kvp.Key] = kvp.Value;
            }

            return Task.FromResult(new NodeExecutionResult
            {
                Success = true,
                OutputData = outputData
            });
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Cache operation failed: {ex.Message}");
            return Task.FromResult(new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    private object? TryParseJsonValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        
        // Try to parse as JSON
        try
        {
            using var doc = JsonDocument.Parse(value);
            // Return the raw JSON string for objects/arrays, primitive values directly
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Number => doc.RootElement.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value // For objects/arrays, keep as JSON string
            };
        }
        catch
        {
            // Not valid JSON, return as string
            return value;
        }
    }

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        
        var result = template;
        
        // Handle {{placeholder}} format
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
}

public class CacheConfig
{
    public string? Operation { get; set; } = "Get";
    public string? PropertyName { get; set; }
    public string? Value { get; set; }
    public bool EnableExpiration { get; set; } = false;
    public int ExpirationMinutes { get; set; } = 60;
}
