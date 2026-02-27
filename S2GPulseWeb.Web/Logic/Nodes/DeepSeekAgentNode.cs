using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;
using System.Linq;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// DeepSeek Agent Node with function/tool calling support.
/// Enables AI to invoke connected workflow nodes as tools.
/// </summary>
public class DeepSeekAgentNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public DeepSeekAgentNode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "DeepSeekAgent";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<DeepSeekAgentConfig>(node.Configuration ?? "{}") ?? new DeepSeekAgentConfig();
        
        // Preserve original prompt values for cost tracking restoration
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "DeepSeek_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            return new NodeExecutionResult { Success = false, ErrorMessage = "DeepSeek API Key is missing. Please configure it in Settings." };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);
        var model = config.Model ?? "deepseek-chat";
        
        // ═══════════════════════════════════════════════════════════════════════════
        // ORCHESTRATOR CONTEXT HANDLING (Ephemeral - from inputData, not persisted)
        // ═══════════════════════════════════════════════════════════════════════════
        
        // Read orchestrator system prompt override (replaces agent's system prompt if provided)
        if (inputData.TryGetValue("_OrchestratorSystemPromptOverride", out var sysOverride) && 
            !string.IsNullOrEmpty(sysOverride?.ToString()))
        {
            systemPrompt = sysOverride.ToString()!;
            Log(node, NodeLogLevel.Info, "Using orchestrator system prompt override");
        }
        
        // Read orchestrator prompt append (ephemeral feedback from steering AI)
        // This comes fresh each call - no accumulation across iterations
        if (inputData.TryGetValue("_OrchestratorPromptAppend", out var promptAppend) && 
            !string.IsNullOrEmpty(promptAppend?.ToString()))
        {
            prompt = $"{prompt}\n\n[Orchestrator Feedback]:\n{promptAppend}";
            Log(node, NodeLogLevel.Info, $"Appended orchestrator feedback: {promptAppend?.ToString()?.Substring(0, Math.Min(100, promptAppend?.ToString()?.Length ?? 0))}...");
        }
        
        // Log if running under orchestrator control
        if (inputData.TryGetValue("_OrchestratorIteration", out var iteration))
        {
            var roleName = inputData.GetValueOrDefault("_OrchestratorRoleName")?.ToString() ?? "Agent";
            Log(node, NodeLogLevel.Info, $"Orchestrated execution: Role={roleName}, Iteration={iteration}");
        }
        
        // ═══════════════════════════════════════════════════════════════════════════
        
        // Default system prompt for multi-tool chaining if user hasn't provided one
        if (string.IsNullOrWhiteSpace(systemPrompt) && config.Tools.Count > 0)
        {
            systemPrompt = "You have access to multiple tools. When the task requires multiple steps, call each tool in sequence. After receiving a tool result, continue calling additional tools until the task is complete.";
        }
        
        // Default example prompt if none provided
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = "First list the files, then read the contents of test.txt, then write a summary.";
        }

        if (config.Tools.Count == 0)
        {
            Log(node, NodeLogLevel.Warning, "No tools configured. Connect tool:* labeled connections from the Orchestrator to enable tool calling.");
        }

        // Build initial messages
        var messages = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new Dictionary<string, object?> { { "role", "system" }, { "content", systemPrompt } });
        }
        messages.Add(new Dictionary<string, object?> { { "role", "user" }, { "content", prompt } });

        // Execute with tool calling loop
        return await ExecuteWithToolCallingAsync(node, inputData, config, messages, model, apiKey, originalPrompt, originalSystemPrompt);
    }

    private async Task<NodeExecutionResult> ExecuteWithToolCallingAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        DeepSeekAgentConfig config,
        List<Dictionary<string, object?>> messages,
        string model,
        string apiKey,
        string originalPrompt,
        string? originalSystemPrompt)
    {
        var toolCallsUsed = 0;
        var allToolResults = new List<object>();
        double totalRunCost = 0;
        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        
        // Build OpenAI-compatible tools array
        object[]? tools = null;
        if (config.Tools.Count > 0)
        {
            tools = config.Tools.Select(t => (object)new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = new
                    {
                        type = "object",
                        properties = t.Parameters
                            .Where(p => p.Value.IsEnabled)  // Only include enabled parameters
                            .ToDictionary(
                                p => p.Key,
                                p => new { type = p.Value.Type, description = p.Value.Description }
                            ),
                        required = t.Required.Where(r => t.Parameters.TryGetValue(r, out var p) && p.IsEnabled).ToList()
                    }
                }
            }).ToArray();

            Log(node, NodeLogLevel.Info, $"Agent Mode with {config.Tools.Count} tools: {string.Join(", ", config.Tools.Select(t => t.Name))}");
        }

        for (int iteration = 0; iteration < config.MaxToolCalls; iteration++)
        {
            Log(node, NodeLogLevel.Info, $"Iteration {iteration + 1}/{config.MaxToolCalls}");

            // Build request
            object requestBody = tools != null
                ? new { model, messages = messages.ToArray(), tools, stream = false }
                : new { model, messages = messages.ToArray(), stream = false };

            // Send request
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            
            HttpResponseMessage response;
            string responseContent;
            try
            {
                response = await _httpClient.SendAsync(request, cts.Token);
                responseContent = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Log(node, NodeLogLevel.Error, $"Request failed: {ex.Message}");
                return new NodeExecutionResult { Success = false, ErrorMessage = ex.Message };
            }

            if (!response.IsSuccessStatusCode)
            {
                Log(node, NodeLogLevel.Error, $"API error: {(int)response.StatusCode}", responseContent);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"DeepSeek API error: {responseContent}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

            // Track usage
            if (jsonResponse.TryGetProperty("usage", out var usage))
            {
                long inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt64() : 0;
                long outputTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt64() : 0;
                double cost = (inputTokens / 1000000.0) * 0.28 + (outputTokens / 1000000.0) * 0.42;
                
                totalInputTokens += inputTokens;
                totalOutputTokens += outputTokens;
                totalRunCost += cost;
            }

            // Parse response
            var choice = jsonResponse.GetProperty("choices")[0];
            var message = choice.GetProperty("message");

            // Check for tool_calls
            if (message.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                var toolCallsList = toolCallsElement.EnumerateArray().ToList();
                Log(node, NodeLogLevel.Info, $"AI requested {toolCallsList.Count} tool call(s)");

                // Add assistant message to history
                messages.Add(JsonSerializer.Deserialize<Dictionary<string, object?>>(message.GetRawText()) ?? new());

                foreach (var toolCall in toolCallsList)
                {
                    toolCallsUsed++;
                    var toolCallId = toolCall.GetProperty("id").GetString() ?? "";
                    var function = toolCall.GetProperty("function");
                    var functionName = function.GetProperty("name").GetString() ?? "";
                    var functionArgs = function.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}";

                    Log(node, NodeLogLevel.Info, $"Executing tool: {functionName}", functionArgs);

                    var toolDef = config.Tools.FirstOrDefault(t => t.Name == functionName);
                    var toolResult = toolDef == null
                        ? JsonSerializer.Serialize(new { error = $"Tool '{functionName}' not found" })
                        : await ExecuteToolNodeAsync(node, inputData, toolDef, functionArgs);

                    Log(node, NodeLogLevel.Info, $"Tool result", toolResult.Length > 500 ? toolResult[..500] + "..." : toolResult);

                    messages.Add(new Dictionary<string, object?>
                    {
                        { "role", "tool" },
                        { "tool_call_id", toolCallId },
                        { "content", toolResult }
                    });

                    allToolResults.Add(new { tool = functionName, result = toolResult });
                }
                continue;
            }

            // No tool calls - AI is done
            var aiResponse = message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";

            UpdateConfigWithCosts(node, config, totalRunCost, totalInputTokens, totalOutputTokens, originalPrompt, originalSystemPrompt);

            Log(node, NodeLogLevel.Info, $"Complete. Tool calls: {toolCallsUsed}, Cost: ${totalRunCost:F5}");
            
            // Log actual AI response content for debugging
            var truncatedResponse = aiResponse.Length > 500 ? aiResponse[..500] + "..." : aiResponse;
            Log(node, NodeLogLevel.Info, "AIResponse content", truncatedResponse);

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AIResponse", aiResponse },
                    { "ModelUsed", model },
                    { "ToolCallsUsed", toolCallsUsed },
                    { "ToolResults", allToolResults },
                    { "TotalCost", totalRunCost }
                }
            };
        }

        // Max iterations
        Log(node, NodeLogLevel.Warning, $"Max tool calls ({config.MaxToolCalls}) reached");
        UpdateConfigWithCosts(node, config, totalRunCost, totalInputTokens, totalOutputTokens, originalPrompt, originalSystemPrompt);

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "AIResponse", "Max tool calls reached" },
                { "ModelUsed", model },
                { "ToolCallsUsed", toolCallsUsed },
                { "MaxToolCallsReached", true }
            }
        };
    }

    private async Task<string> ExecuteToolNodeAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        DeepSeekAgentToolDefinition toolDef,
        string argumentsJson)
    {
        try
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson) ?? new();
            var paramOverrides = new Dictionary<string, object?>();
            
            foreach (var arg in arguments)
            {
                paramOverrides[arg.Key] = arg.Value.ValueKind switch
                {
                    JsonValueKind.String => arg.Value.GetString(),
                    JsonValueKind.Number => arg.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => arg.Value.GetRawText()
                };
            }

            // Extract workflow context - need explicit unboxing for Guid
            Guid? workflowId = null;
            if (inputData.TryGetValue("_WorkflowId", out var wfIdObj) && wfIdObj is Guid wfId)
            {
                workflowId = wfId;
            }
            var executionService = inputData.GetValueOrDefault("_WorkflowExecutionService");

            // Debug: Log what we have
            Log(node, NodeLogLevel.Info, $"Workflow context: WorkflowId={workflowId}, ExecutionService={executionService != null}");

            if (workflowId == null || executionService == null)
            {
                Log(node, NodeLogLevel.Error, $"Missing workflow context. WorkflowId present: {workflowId != null}, ExecutionService present: {executionService != null}");
                return JsonSerializer.Serialize(new { error = "Workflow context not available" });
            }

            // Apply NodeConfigField mappings to convert AI param names to config property names
            // This ensures both workflow and direct execution paths use the same mapped names
            var mappedOverrides = MapParametersToConfigFields(toolDef, paramOverrides);

            // Try ExecuteToolWithParametersAsync first
            var executeMethod = executionService.GetType().GetMethod("ExecuteToolWithParametersAsync");
            if (executeMethod != null)
            {
                var result = executeMethod.Invoke(executionService, new object?[] { workflowId.Value, toolDef.EntryNodeId, mappedOverrides, inputData });
                if (result is Task<Dictionary<string, object?>> taskWithResult)
                {
                    var outputData = await taskWithResult;
                    
                    // Check if it returned an error (e.g., "Workflow not found" for Run Node mode)
                    if (outputData.TryGetValue("Error", out var error) && error != null)
                    {
                        Log(node, NodeLogLevel.Info, $"Workflow service returned error: {error}, trying direct execution");
                        return await ExecuteToolDirectlyAsync(node, inputData, toolDef, paramOverrides);
                    }
                    
                    return JsonSerializer.Serialize(outputData);
                }
            }

            // Fallback to direct execution
            return await ExecuteToolDirectlyAsync(node, inputData, toolDef, paramOverrides);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Tool error: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Direct tool execution using ExecutorFactory - for "Run Node" mode where workflow isn't in running state.
    /// Uses canvas nodes configuration passed from Designer.
    /// </summary>
    private async Task<string> ExecuteToolDirectlyAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        DeepSeekAgentToolDefinition toolDef,
        Dictionary<string, object?> paramOverrides)
    {
        try
        {
            // Get ExecutorFactory and CanvasNodes from inputData
            var factoryObj = inputData.GetValueOrDefault("_ExecutorFactory");
            var canvasNodesObj = inputData.GetValueOrDefault("_CanvasNodes");
            var userId = inputData.GetValueOrDefault("_UserId")?.ToString() ?? "";
            
            if (factoryObj == null || canvasNodesObj == null)
            {
                return JsonSerializer.Serialize(new { error = "Direct execution context not available. Run the full workflow instead." });
            }
            
            // Find the tool node in canvas nodes using reflection (avoid compile-time coupling)
            var canvasNodes = canvasNodesObj as System.Collections.IEnumerable;
            if (canvasNodes == null)
            {
                return JsonSerializer.Serialize(new { error = "Canvas nodes not available" });
            }
            
            object? toolCanvasNode = null;
            foreach (var canvasNode in canvasNodes)
            {
                var idProperty = canvasNode.GetType().GetProperty("Id");
                if (idProperty != null)
                {
                    var nodeId = idProperty.GetValue(canvasNode);
                    if (nodeId is Guid guid && guid == toolDef.EntryNodeId)
                    {
                        toolCanvasNode = canvasNode;
                        break;
                    }
                }
            }
            
            if (toolCanvasNode == null)
            {
                return JsonSerializer.Serialize(new { error = $"Tool node {toolDef.EntryNodeId} not found on canvas" });
            }
            
            // Extract node properties via reflection
            var nodeTypeProperty = toolCanvasNode.GetType().GetProperty("NodeType");
            var nameProperty = toolCanvasNode.GetType().GetProperty("Name");
            var configProperty = toolCanvasNode.GetType().GetProperty("Configuration");
            
            var nodeType = nodeTypeProperty?.GetValue(toolCanvasNode)?.ToString() ?? "";
            var nodeName = nameProperty?.GetValue(toolCanvasNode)?.ToString() ?? "";
            var config = configProperty?.GetValue(toolCanvasNode)?.ToString() ?? "{}";
            
            // Inject parameters directly into config JSON properties
            // AI provides params like "operation", "path", "content" - map to config properties
            if (paramOverrides.Count > 0)
            {
                try
                {
                    var configDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config) 
                        ?? new Dictionary<string, JsonElement>();
                    
                    // Create a new dict with object values for modification
                    var modifiableConfig = new Dictionary<string, object?>();
                    foreach (var kvp in configDict)
                    {
                        modifiableConfig[kvp.Key] = kvp.Value.ValueKind switch
                        {
                            JsonValueKind.String => kvp.Value.GetString(),
                            JsonValueKind.Number => kvp.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => kvp.Value.GetRawText()
                        };
                    }
                    
                    // Build dynamic parameter mappings from tool definition
                    // Each param has NodeConfigField that tells us which config property to set
                    var dynamicMappings = toolDef.Parameters
                        .Where(p => p.Value.IsEnabled && !string.IsNullOrEmpty(p.Value.NodeConfigField))
                        .ToDictionary(
                            p => p.Key, 
                            p => p.Value.NodeConfigField, 
                            StringComparer.OrdinalIgnoreCase
                        );
                    
                    // Fallback mappings for when tool definition doesn't have mappings
                    // (e.g., manually added tools or upgraded configs)
                    var fallbackMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "operation", "Operation" },
                        { "path", "FilePath" },
                        { "filepath", "FilePath" },
                        { "file_path", "FilePath" },
                        { "filename", "FilePath" },
                        { "content", "Content" },
                        { "text", "Content" },
                        { "data", "Content" },
                        { "folder", "FolderPath" },
                        { "folderpath", "FolderPath" },
                        { "folder_path", "FolderPath" },
                        { "query", "Query" },
                        { "sql", "Query" },
                        { "url", "Url" },
                        { "prompt", "Prompt" },
                        { "message", "Prompt" },
                    };
                    
                    foreach (var param in paramOverrides)
                    {
                        // First try dynamic mappings from tool definition
                        string configKey;
                        if (dynamicMappings.TryGetValue(param.Key, out var mapped))
                        {
                            configKey = mapped;
                        }
                        else if (fallbackMappings.TryGetValue(param.Key, out var fallbackMapped))
                        {
                            // Use fallback if no dynamic mapping
                            configKey = fallbackMapped;
                        }
                        else
                        {
                            // Last resort: PascalCase conversion
                            configKey = ToPascalCase(param.Key);
                        }
                        
                        var value = param.Value;
                        
                        // Normalize operation values for S2G Storage and other nodes
                        if (configKey == "Operation" && value is string opValue)
                        {
                            value = NormalizeOperationValue(opValue);
                        }
                        
                        // When setting Content, clear ContentBase64 so node uses text content
                        if (configKey == "Content")
                        {
                            modifiableConfig.Remove("ContentBase64");
                        }
                        
                        modifiableConfig[configKey] = value;
                        Log(node, NodeLogLevel.Info, $"Injected param: {param.Key} -> {configKey} = {value}");
                    }
                    
                    config = JsonSerializer.Serialize(modifiableConfig);
                }
                catch (Exception ex)
                {
                    Log(node, NodeLogLevel.Warning, $"Config injection failed, using placeholder fallback: {ex.Message}");
                    // Fallback to placeholder replacement
                    foreach (var param in paramOverrides)
                    {
                        var placeholder = $"{{{{{param.Key}}}}}";
                        config = config.Replace(placeholder, param.Value?.ToString() ?? "");
                    }
                }
            }
            
            // Create executor and execute
            var createMethod = factoryObj.GetType().GetMethod("CreateExecutor");
            if (createMethod == null)
            {
                return JsonSerializer.Serialize(new { error = "Cannot create executor" });
            }
            
            var executor = createMethod.Invoke(factoryObj, new object[] { nodeType });
            if (executor == null)
            {
                return JsonSerializer.Serialize(new { error = $"No executor for type: {nodeType}" });
            }
            
            // Build workflow node
            var workflowNode = new WorkflowNode
            {
                Id = toolDef.EntryNodeId,
                NodeType = nodeType,
                Name = nodeName,
                Configuration = config
            };
            
            // Merge input data with parameters
            var toolInputData = new Dictionary<string, object?>(inputData);
            foreach (var param in paramOverrides)
            {
                toolInputData[param.Key] = param.Value;
            }
            
            // Execute via reflection
            var executeMethod = executor.GetType().GetMethod("ExecuteAsync");
            if (executeMethod == null)
            {
                return JsonSerializer.Serialize(new { error = "Executor has no ExecuteAsync method" });
            }
            
            var task = executeMethod.Invoke(executor, new object[] { workflowNode, toolInputData, userId });
            if (task is Task<NodeExecutionResult> resultTask)
            {
                var result = await resultTask;
                Log(node, NodeLogLevel.Info, $"Direct tool execution {(result.Success ? "succeeded" : "failed")}: {nodeName}");
                
                if (result.OutputData != null)
                {
                    return JsonSerializer.Serialize(result.OutputData);
                }
                
                return JsonSerializer.Serialize(new { success = result.Success, error = result.ErrorMessage });
            }
            
            return JsonSerializer.Serialize(new { status = "executed", node = nodeName });
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Direct tool execution error: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private void UpdateConfigWithCosts(WorkflowNode node, DeepSeekAgentConfig config, double cost, long input, long output, string origPrompt, string? origSystem)
    {
        config.Cost += cost;
        config.InputTokens += input;
        config.OutputTokens += output;
        config.Prompt = origPrompt;
        config.SystemPrompt = origSystem;
        node.Configuration = JsonSerializer.Serialize(config);
        _executionManager?.NotifyConfigurationUpdated(node.Id, node.Configuration);
    }

    public override List<string> GetOutputParameters() => new() { "AIResponse", "ModelUsed", "ToolCallsUsed", "ToolResults", "TotalCost" };

    private string ReplacePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        foreach (var kvp in data)
            template = template.Replace($"{{{kvp.Key}}}", kvp.Value?.ToString() ?? "");
        return template;
    }

    /// <summary>
    /// Converts snake_case or camelCase to PascalCase for config property names.
    /// </summary>
    private string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        var parts = input.Split('_');
        var result = string.Join("", parts.Select(p => 
            string.IsNullOrEmpty(p) ? "" : char.ToUpper(p[0]) + p.Substring(1).ToLower()));
        
        // If no underscores and starts lowercase, just capitalize first letter
        if (!input.Contains('_') && char.IsLower(input[0]))
            return char.ToUpper(input[0]) + input.Substring(1);
        
        return result;
    }

    /// <summary>
    /// Normalizes AI-provided operation values to expected node operation values.
    /// </summary>
    private string NormalizeOperationValue(string operation)
    {
        var normalized = operation.ToLowerInvariant().Replace("_", "").Replace("-", "");
        
        return normalized switch
        {
            // S2G Storage operations
            "write" or "writefile" or "create" or "createfile" or "save" or "savefile" => "Write",
            "read" or "readfile" or "get" or "getfile" or "load" or "loadfile" => "Read",
            "edit" or "editfile" or "update" or "updatefile" or "modify" => "Edit",
            "delete" or "deletefile" or "remove" or "removefile" => "Delete",
            "list" or "listfiles" or "dir" or "ls" => "List",
            "createfolder" or "mkdir" or "makedir" or "newfolder" => "CreateFolder",
            
            // HTTP operations
            "post" => "POST",
            "put" => "PUT",
            "patch" => "PATCH",
            
            // Default: return as-is with first letter capitalized
            _ => char.ToUpper(operation[0]) + operation.Substring(1).ToLower()
        };
    }

    /// <summary>
    /// Maps AI-provided parameter names to node configuration property names.
    /// Uses NodeConfigField from tool definition, with fallback mappings for common patterns.
    /// </summary>
    private Dictionary<string, object?> MapParametersToConfigFields(
        DeepSeekAgentToolDefinition toolDef,
        Dictionary<string, object?> paramOverrides)
    {
        var mapped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        
        // Build dynamic mappings from tool definition
        var dynamicMappings = toolDef.Parameters
            .Where(p => p.Value.IsEnabled && !string.IsNullOrEmpty(p.Value.NodeConfigField))
            .ToDictionary(
                p => p.Key, 
                p => p.Value.NodeConfigField, 
                StringComparer.OrdinalIgnoreCase
            );
        
        // Fallback mappings for common AI param names
        var fallbackMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "operation", "Operation" },
            { "path", "FilePath" },
            { "filepath", "FilePath" },
            { "file_path", "FilePath" },
            { "filename", "FilePath" },
            { "content", "Content" },
            { "text", "Content" },
            { "data", "Content" },
            { "folder", "FolderPath" },
            { "folderpath", "FolderPath" },
            { "folder_path", "FolderPath" },
            { "query", "Query" },
            { "sql", "Query" },
            { "url", "Url" },
            { "prompt", "Prompt" },
            { "message", "Prompt" },
        };
        
        foreach (var param in paramOverrides)
        {
            string configKey;
            object? value = param.Value;
            
            // First try dynamic mappings from tool definition
            if (dynamicMappings.TryGetValue(param.Key, out var dynamicMapped))
            {
                configKey = dynamicMapped;
            }
            else if (fallbackMappings.TryGetValue(param.Key, out var fallbackMapped))
            {
                configKey = fallbackMapped;
            }
            else
            {
                // Use PascalCase conversion as last resort
                configKey = ToPascalCase(param.Key);
            }
            
            // Normalize operation values
            if (configKey.Equals("Operation", StringComparison.OrdinalIgnoreCase) && value is string opStr)
            {
                value = NormalizeOperationValue(opStr);
            }
            
            mapped[configKey] = value;
        }
        
        return mapped;
    }
}

public class DeepSeekAgentConfig
{
    public string? Model { get; set; } = "deepseek-chat";
    public string Prompt { get; set; } = "";
    public string? SystemPrompt { get; set; } = "You have access to multiple tools. When the task requires multiple steps, call each tool in sequence. After receiving a tool result, continue calling additional tools until the task is complete.";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxToolCalls { get; set; } = 10;
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;
    public List<DeepSeekAgentToolDefinition> Tools { get; set; } = new();
}

public class DeepSeekAgentToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid EntryNodeId { get; set; }
    public Dictionary<string, DeepSeekAgentToolParameter> Parameters { get; set; } = new();
    public List<string> Required { get; set; } = new();
    /// <summary>Output values that agent can read for recursive feedback</summary>
    public List<DeepSeekAgentToolOutput> Outputs { get; set; } = new();
    /// <summary>UI collapse state for field configuration</summary>
    public bool IsExpanded { get; set; } = false;
}

public class DeepSeekAgentToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    /// <summary>User can disable this parameter from being exposed to AI</summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>Maps to the node configuration field name (e.g., "FilePath", "Query")</summary>
    public string NodeConfigField { get; set; } = "";
}

/// <summary>
/// Defines an output value that the agent can read from tool execution results.
/// </summary>
public class DeepSeekAgentToolOutput
{
    /// <summary>Placeholder format: {{NodeName.FieldName}}</summary>
    public string Placeholder { get; set; } = "";
    /// <summary>Data type: string, array, json, int, bool</summary>
    public string DataType { get; set; } = "string";
    public string Description { get; set; } = "";
    /// <summary>User can disable this output from being returned to AI</summary>
    public bool IsEnabled { get; set; } = true;
}

