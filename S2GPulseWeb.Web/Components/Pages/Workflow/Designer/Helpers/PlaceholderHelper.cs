using System.Text.Json;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Helper class for placeholder resolution and JSON path extraction.
/// </summary>
public class PlaceholderHelper
{
    private readonly CacheStorageService _cacheStorageService;
    private readonly CustomNodeService _customNodeService;

    public PlaceholderHelper(CacheStorageService cacheStorageService, CustomNodeService customNodeService)
    {
        _cacheStorageService = cacheStorageService;
        _customNodeService = customNodeService;
    }

    /// <summary>
    /// Resolves placeholders in a configuration string using data from canvas nodes.
    /// </summary>
    public string? ResolvePlaceholders(string? config, CanvasNode targetNode, List<CanvasNode> canvasNodes)
    {
        if (string.IsNullOrEmpty(config)) return config;

        // Find all placeholders in the config using regex
        var placeholderRegex = new System.Text.RegularExpressions.Regex(@"\{\{([^}]+)\}\}");
        var matches = placeholderRegex.Matches(config);
        
        var result = config;
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var fullPlaceholder = match.Value; // e.g., {{Request.Body.access_token}}
            var key = match.Groups[1].Value;   // e.g., Request.Body.access_token
            
            var parts = key.Split('.', 2); // Split into NodeName and rest
            if (parts.Length < 2) continue;
            
            var sourceNodeName = parts[0];
            var propertyPath = parts[1]; // e.g., Body.access_token or just Body
            
            var sourceNode = canvasNodes.FirstOrDefault(n => n.Name == sourceNodeName);
            if (sourceNode == null) continue;
            
            // Split the property path to check for JSON path
            var pathParts = propertyPath.Split('.', 2);
            var outputPropertyName = pathParts[0]; // e.g., Body
            var jsonPath = pathParts.Length > 1 ? pathParts[1] : null; // e.g., access_token or null
            
            if (!sourceNode.OutputData.TryGetValue(outputPropertyName, out var val)) continue;
            
            string? resolvedValue = null;
            
            if (jsonPath != null && val != null)
            {
                // Try to extract JSON path from the value
                resolvedValue = ExtractJsonPath(val.ToString() ?? "", jsonPath);
            }
            else
            {
                resolvedValue = val?.ToString() ?? "";
            }
            
            if (resolvedValue != null)
            {
                var escapedVal = JsonEscape(resolvedValue);
                result = result.Replace(fullPlaceholder, escapedVal);
            }
        }
        return result;
    }

    /// <summary>
    /// Extracts a value from a JSON string using a dot-separated path.
    /// </summary>
    public string? ExtractJsonPath(string json, string path)
    {
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

    /// <summary>
    /// Escapes a string for use in JSON.
    /// </summary>
    public string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var json = JsonSerializer.Serialize(value);
        // Remove the leading and trailing double quotes
        return json.Substring(1, json.Length - 2);
    }

    /// <summary>
    /// Gets the list of available placeholders for a node based on connected upstream nodes.
    /// </summary>
    public List<string> GetAvailablePlaceholders(
        CanvasNode node, 
        List<CanvasNode> canvasNodes, 
        List<NodeConnection> connections,
        Guid? currentWorkflowId,
        bool showAllPlaceholders = false)
    {
        var placeholders = new List<string>();
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();

        // Get immediate predecessor node IDs (nodes that connect INTO this node)
        var immediateSourceIds = connections.Where(c => c.TargetId == node.Id).Select(c => c.SourceId).ToHashSet();
        
        // Also include nodes connected via "reader" connections (where current node has a reader connection TO another node)
        var readerSourceIds = connections
            .Where(c => c.SourceId == node.Id && string.Equals(c.Label, "reader", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.TargetId);
        foreach (var readerId in readerSourceIds)
        {
            immediateSourceIds.Add(readerId);
        }
        
        // Also include nodes connected via "storage" connections (where current node has a storage connection TO a StorageTable)
        var storageSourceIds = connections
            .Where(c => c.SourceId == node.Id && string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.TargetId);
        foreach (var storageId in storageSourceIds)
        {
            immediateSourceIds.Add(storageId);
        }
        
        foreach (var sourceId in immediateSourceIds) queue.Enqueue(sourceId);

        while (queue.Count > 0)
        {
            var sourceId = queue.Dequeue();
            if (visited.Contains(sourceId)) continue;
            visited.Add(sourceId);

            var sourceNode = canvasNodes.FirstOrDefault(n => n.Id == sourceId);
            if (sourceNode != null)
            {
                var params_ = NodeHelper.GetOutputParametersForType(sourceNode.NodeType);
                foreach (var p in params_) placeholders.Add($"{{{{{sourceNode.Name}.{p}}}}}");
                
                // Add dynamic properties from last execution samples
                if (!string.IsNullOrEmpty(sourceNode.Configuration))
                {
                    try
                    {
                        var config = JsonSerializer.Deserialize<Dictionary<string, object?>>(sourceNode.Configuration);
                        if (config != null)
                        {
                            // SQL nodes: add columns from ExpectedColumns or LastExecutionColumns
                            if (sourceNode.NodeType == "SqlServer")
                            {
                                AddSqlColumnPlaceholders(config, sourceNode.Name, placeholders);
                            }
                            // HTTP Listener: add body properties from LastBodySample
                            else if (sourceNode.NodeType == "HttpListener")
                            {
                                AddHttpListenerPlaceholders(config, sourceNode.Name, placeholders);
                            }
                            // HTTP Request: add response body properties from LastResponseSample
                            else if (sourceNode.NodeType == "HttpRequest")
                            {
                                AddHttpRequestPlaceholders(config, sourceNode.Name, placeholders);
                            }
                            // Azure Queue Monitor: add message properties from LastMessageSample
                            else if (sourceNode.NodeType == "AzureQueueMonitor")
                            {
                                AddAzureQueueMonitorPlaceholders(config, sourceNode.Name, placeholders);
                            }
                            // Azure Storage (Table): add column properties from DiscoveredColumns
                            else if (sourceNode.NodeType == "AzureStorage")
                            {
                                AddAzureStoragePlaceholders(config, sourceNode.Name, placeholders);
                            }
                        }
                    }
                    catch { }
                }
                
                // For Cache nodes: add dynamically stored property names from OutputData OR CacheStorageService
                if (sourceNode.NodeType == "Cache")
                {
                    AddCachePlaceholders(sourceNode, currentWorkflowId, placeholders);
                }
                // For StorageTable nodes: add column names from configuration
                else if (sourceNode.NodeType == "StorageTable")
                {
                    AddStorageTablePlaceholders(sourceNode.Configuration, sourceNode.Name, placeholders);
                }
                // For Loop nodes: add dynamic item properties from LastInputArraySample
                else if (sourceNode.NodeType == "Loop")
                {
                    AddLoopItemPlaceholders(sourceNode.Configuration, sourceNode.Name, placeholders);
                }
                // For Custom nodes: add output parameters from node definition
                else if (NodeHelper.IsCustomNode(sourceNode.NodeType))
                {
                    AddCustomNodePlaceholders(sourceNode.NodeType, sourceNode.Name, placeholders);
                }
                
                // Only traverse further if showAllPlaceholders is enabled
                if (showAllPlaceholders)
                {
                    foreach (var conn in connections.Where(c => c.TargetId == sourceId)) queue.Enqueue(conn.SourceId);
                }
            }
        }
        return placeholders.Distinct().ToList();
    }

    private void AddSqlColumnPlaceholders(Dictionary<string, object?> config, string nodeName, List<string> placeholders)
    {
        // First try ExpectedColumns (user-defined), then fall back to LastExecutionColumns (auto-detected)
        string? columnsStr = null;
        if (config.TryGetValue("ExpectedColumns", out var expected) && expected != null)
        {
            columnsStr = expected.ToString();
        }
        if (string.IsNullOrWhiteSpace(columnsStr) && config.TryGetValue("LastExecutionColumns", out var detected) && detected != null)
        {
            columnsStr = detected.ToString();
        }
        
        if (!string.IsNullOrWhiteSpace(columnsStr))
        {
            var columns = columnsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var col in columns)
            {
                placeholders.Add($"{{{{{nodeName}.{col}}}}}");
            }
        }
    }

    private void AddHttpListenerPlaceholders(Dictionary<string, object?> config, string nodeName, List<string> placeholders)
    {
        // Add body property placeholders from LastBodySample
        if (config.TryGetValue("LastBodySample", out var bodySample) && bodySample != null)
        {
            var bodyJson = bodySample.ToString();
            var properties = JsonPropertyExtractor.ExtractPropertyPaths(bodyJson);
            var bodyPlaceholders = JsonPropertyExtractor.ToPlaceholders(properties, nodeName, "Body");
            placeholders.AddRange(bodyPlaceholders);
        }
        
        // Add header property placeholders from LastHeadersSample
        if (config.TryGetValue("LastHeadersSample", out var headersSample) && headersSample != null)
        {
            var headersJson = headersSample.ToString();
            if (!string.IsNullOrWhiteSpace(headersJson))
            {
                try
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                    if (headers != null)
                    {
                        foreach (var key in headers.Keys)
                        {
                            placeholders.Add($"{{{{{nodeName}.HeadersJson.{key}}}}}");
                        }
                    }
                }
                catch { }
            }
        }

        // Add query param placeholders from LastQueryParams
        if (config.TryGetValue("LastQueryParams", out var queryParams) && queryParams != null)
        {
            var paramsStr = queryParams.ToString();
            if (!string.IsNullOrWhiteSpace(paramsStr))
            {
                var params_ = paramsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var param in params_)
                {
                    placeholders.Add($"{{{{{nodeName}.{param}}}}}");
                }
            }
        }
    }

    private void AddHttpRequestPlaceholders(Dictionary<string, object?> config, string nodeName, List<string> placeholders)
    {
        // Add response body property placeholders from LastResponseSample
        if (config.TryGetValue("LastResponseSample", out var responseSample) && responseSample != null)
        {
            var responseJson = responseSample.ToString();
            var properties = JsonPropertyExtractor.ExtractPropertyPaths(responseJson);
            var responsePlaceholders = JsonPropertyExtractor.ToPlaceholders(properties, nodeName, "Body");
            placeholders.AddRange(responsePlaceholders);
        }

        // Add response header property placeholders from LastResponseHeadersSample
        if (config.TryGetValue("LastResponseHeadersSample", out var responseHeadersSample) && responseHeadersSample != null)
        {
            var headersJson = responseHeadersSample.ToString();
            if (!string.IsNullOrWhiteSpace(headersJson))
            {
                try
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                    if (headers != null)
                    {
                        foreach (var key in headers.Keys)
                        {
                            placeholders.Add($"{{{{{nodeName}.ResponseHeadersJson.{key}}}}}");
                        }
                    }
                }
                catch { }
            }
        }
    }

    private void AddAzureQueueMonitorPlaceholders(Dictionary<string, object?> config, string nodeName, List<string> placeholders)
    {
        // Add message property placeholders from LastMessageSample
        if (config.TryGetValue("LastMessageSample", out var messageSample) && messageSample != null)
        {
            var messageJson = messageSample.ToString();
            if (!string.IsNullOrWhiteSpace(messageJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(messageJson);
                    
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        // For JSON objects, extract properties directly (no prefix)
                        var properties = JsonPropertyExtractor.ExtractPropertyPaths(messageJson);
                        // Use empty output property - Queue Monitor properties are at root level
                        foreach (var prop in properties)
                        {
                            placeholders.Add($"{{{{{nodeName}.{prop.Key}}}}}");
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        // For JSON arrays, add items and itemCount placeholders
                        placeholders.Add($"{{{{{nodeName}.items}}}}");
                        placeholders.Add($"{{{{{nodeName}.itemCount}}}}");
                        
                        // Also extract properties from the first element if it's an object
                        if (doc.RootElement.GetArrayLength() > 0)
                        {
                            var firstElement = doc.RootElement[0];
                            if (firstElement.ValueKind == JsonValueKind.Object)
                            {
                                var firstElementJson = firstElement.GetRawText();
                                var properties = JsonPropertyExtractor.ExtractPropertyPaths(firstElementJson);
                                foreach (var prop in properties)
                                {
                                    placeholders.Add($"{{{{{nodeName}.items[].{prop.Key}}}}}");
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }

    private void AddAzureStoragePlaceholders(Dictionary<string, object?> config, string nodeName, List<string> placeholders)
    {
        // Add column placeholders from DiscoveredColumns (persisted from discovery)
        if (config.TryGetValue("DiscoveredColumns", out var discoveredColumns) && discoveredColumns != null)
        {
            var columnsStr = discoveredColumns.ToString();
            if (!string.IsNullOrWhiteSpace(columnsStr))
            {
                var columns = columnsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var col in columns)
                {
                    placeholders.Add($"{{{{{nodeName}.{col}}}}}");
                }
            }
        }
        
        // Also try to extract from LastResultSample if available (from runtime)
        if (config.TryGetValue("LastResultSample", out var resultSample) && resultSample != null)
        {
            var resultJson = resultSample.ToString();
            if (!string.IsNullOrWhiteSpace(resultJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(resultJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        // Get properties from first row
                        var firstRow = doc.RootElement[0];
                        if (firstRow.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in firstRow.EnumerateObject())
                            {
                                placeholders.Add($"{{{{{nodeName}.{prop.Name}}}}}");
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }

    private void AddCachePlaceholders(CanvasNode sourceNode, Guid? currentWorkflowId, List<string> placeholders)
    {
        var cacheKeys = new HashSet<string>();
        
        // First, check OutputData from current session
        foreach (var key in sourceNode.OutputData.Keys)
        {
            // Skip internal properties that are already in the static list
            if (key == "CacheValue" || key == "CacheKeys" || key == "CacheData" || key == "OperationResult")
                continue;
            
            cacheKeys.Add(key);
        }
        
        // Check CacheStorageService for persisted properties
        // Search ALL cache data to handle workflow ID mismatches
        var allCacheData = _cacheStorageService.GetAllCacheData();
        
        // Try exact workflow + node match first
        if (currentWorkflowId.HasValue && allCacheData.TryGetValue(currentWorkflowId.Value, out var workflowCache))
        {
            if (workflowCache.TryGetValue(sourceNode.Id, out var nodeCache))
            {
                foreach (var propKey in nodeCache.Keys)
                {
                    cacheKeys.Add(propKey);
                }
            }
        }
        
        // If still no keys found, search ALL workflows for ANY cache node
        if (!cacheKeys.Any())
        {
            foreach (var wfKvp in allCacheData)
            {
                foreach (var nodeKvp in wfKvp.Value)
                {
                    foreach (var propKey in nodeKvp.Value.Keys)
                    {
                        cacheKeys.Add(propKey);
                    }
                }
            }
        }
        
        // Add unique keys as placeholders
        foreach (var key in cacheKeys)
        {
            placeholders.Add($"{{{{{sourceNode.Name}.{key}}}}}");
        }
    }

    private void AddStorageTablePlaceholders(string? configuration, string nodeName, List<string> placeholders)
    {
        if (string.IsNullOrEmpty(configuration)) return;
        
        try
        {
            using var doc = JsonDocument.Parse(configuration);
            if (doc.RootElement.TryGetProperty("Columns", out var columnsElement) && 
                columnsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var column in columnsElement.EnumerateArray())
                {
                    if (column.TryGetProperty("Name", out var nameElement))
                    {
                        var columnName = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(columnName))
                        {
                            placeholders.Add($"{{{{{nodeName}.{columnName}}}}}");
                        }
                    }
                }
            }
        }
        catch
        {
            // Invalid configuration JSON - ignore
        }
    }

    private void AddLoopItemPlaceholders(string? configuration, string nodeName, List<string> placeholders)
    {
        if (string.IsNullOrEmpty(configuration)) return;
        
        try
        {
            using var doc = JsonDocument.Parse(configuration);
            
            // Get LastInputArraySample from configuration
            if (doc.RootElement.TryGetProperty("LastInputArraySample", out var sampleElement) && 
                sampleElement.ValueKind == JsonValueKind.String)
            {
                var sampleJson = sampleElement.GetString();
                if (!string.IsNullOrEmpty(sampleJson))
                {
                    // Extract properties from the sample (which is the first array element)
                    var properties = JsonPropertyExtractor.ExtractPropertyPaths(sampleJson);
                    
                    // Add placeholders for each top-level property (skip nested [0] paths)
                    foreach (var kvp in properties)
                    {
                        var path = kvp.Key;
                        var info = kvp.Value;
                        
                        // Skip array access paths like "items[0].prop" - we want direct properties
                        if (path.Contains("["))
                            continue;
                        
                        // For arrays that have nested structure, add with [array] marker for UI styling
                        // This allows the PlaceholderList to show special styling for array properties
                        if (info.IsArray)
                        {
                            placeholders.Add($"{{{{{nodeName}.{path}}}}}[array]");
                        }
                        else
                        {
                            placeholders.Add($"{{{{{nodeName}.{path}}}}}");
                        }
                    }
                }
            }
        }
        catch
        {
            // Invalid configuration JSON - ignore
        }
    }

    /// <summary>
    /// Adds placeholders from custom node output parameters.
    /// </summary>
    private void AddCustomNodePlaceholders(string nodeType, string nodeName, List<string> placeholders)
    {
        try
        {
            // Get the custom node definition from cache
            var definition = _customNodeService.GetDefinitionByKeySync(nodeType);
            if (definition == null)
            {
                // Try async as fallback (blocking)
                definition = _customNodeService.GetDefinitionByKeyAsync(nodeType).GetAwaiter().GetResult();
            }
            
            if (definition?.OutputParameters != null)
            {
                foreach (var param in definition.OutputParameters.OrderBy(p => p.DisplayOrder))
                {
                    placeholders.Add($"{{{{{nodeName}.{param.ParameterName}}}}}");
                }
            }
        }
        catch
        {
            // Custom node definition not found - ignore
        }
    }

    /// <summary>
    /// Updates node configuration with execution samples for dynamic placeholder generation.
    /// </summary>
    public void UpdateNodeConfigWithSamples(CanvasNode node, Dictionary<string, object?> outputData)
    {
        try
        {
            var config = string.IsNullOrEmpty(node.Configuration)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(node.Configuration) ?? new();

            bool updated = false;

            // SQL Node: _DetectedColumns -> LastExecutionColumns
            if (outputData.TryGetValue("_DetectedColumns", out var detectedColumns) && detectedColumns != null)
            {
                config["LastExecutionColumns"] = detectedColumns.ToString();
                updated = true;
            }

            // HTTP Listener: _BodySample -> LastBodySample
            if (outputData.TryGetValue("_BodySample", out var bodySample) && bodySample != null)
            {
                // Truncate body sample to avoid storing huge payloads (max 10KB)
                var bodySampleStr = bodySample.ToString() ?? "";
                if (bodySampleStr.Length > 10240)
                {
                    bodySampleStr = bodySampleStr.Substring(0, 10240);
                }
                config["LastBodySample"] = bodySampleStr;
                updated = true;
            }

            // HTTP Listener: _QueryParamsSample -> LastQueryParams
            if (outputData.TryGetValue("_QueryParamsSample", out var queryParams) && queryParams != null)
            {
                config["LastQueryParams"] = queryParams.ToString();
                updated = true;
            }

            // HTTP Listener: _HeadersSample -> LastHeadersSample
            if (outputData.TryGetValue("_HeadersSample", out var headersSample) && headersSample != null)
            {
                var headersSampleStr = headersSample.ToString() ?? "";
                if (headersSampleStr.Length > 10240)
                {
                    headersSampleStr = headersSampleStr.Substring(0, 10240);
                }
                config["LastHeadersSample"] = headersSampleStr;
                updated = true;
            }

            // HTTP Request: _ResponseSample -> LastResponseSample
            if (outputData.TryGetValue("_ResponseSample", out var responseSample) && responseSample != null)
            {
                // Truncate response sample to avoid storing huge payloads (max 10KB)
                var responseSampleStr = responseSample.ToString() ?? "";
                if (responseSampleStr.Length > 10240)
                {
                    responseSampleStr = responseSampleStr.Substring(0, 10240);
                }
                config["LastResponseSample"] = responseSampleStr;
                updated = true;
            }

            // HTTP Request: _ResponseHeadersSample -> LastResponseHeadersSample
            if (outputData.TryGetValue("_ResponseHeadersSample", out var responseHeadersSample) && responseHeadersSample != null)
            {
                var responseHeadersStr = responseHeadersSample.ToString() ?? "";
                if (responseHeadersStr.Length > 10240)
                {
                    responseHeadersStr = responseHeadersStr.Substring(0, 10240);
                }
                config["LastResponseHeadersSample"] = responseHeadersStr;
                updated = true;
            }

            // Azure Queue Monitor: _MessageSample -> LastMessageSample
            if (outputData.TryGetValue("_MessageSample", out var messageSample) && messageSample != null)
            {
                // Truncate message sample to avoid storing huge payloads (max 10KB)
                var messageSampleStr = messageSample.ToString() ?? "";
                if (messageSampleStr.Length > 10240)
                {
                    messageSampleStr = messageSampleStr.Substring(0, 10240);
                }
                config["LastMessageSample"] = messageSampleStr;
                updated = true;
            }

            // Azure Storage (Table): _ResultSample -> LastResultSample
            if (outputData.TryGetValue("_ResultSample", out var resultSample) && resultSample != null)
            {
                // Truncate result sample to avoid storing huge payloads (max 10KB)
                var resultSampleStr = resultSample.ToString() ?? "";
                if (resultSampleStr.Length > 10240)
                {
                    resultSampleStr = resultSampleStr.Substring(0, 10240);
                }
                config["LastResultSample"] = resultSampleStr;
                updated = true;
            }

            if (updated)
            {
                node.Configuration = JsonSerializer.Serialize(config);
            }
        }
        catch (Exception ex)
        {
            // Don't fail execution due to sample storage issues
            Console.WriteLine($"Failed to update node config with samples: {ex.Message}");
        }
    }
}
