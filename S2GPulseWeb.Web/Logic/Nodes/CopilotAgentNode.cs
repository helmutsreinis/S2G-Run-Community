using System.Text.Json;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// GitHub Copilot Agent Node with function/tool calling support.
/// Uses the user's Copilot subscription via OAuth connection.
/// </summary>
public class CopilotAgentNode : BaseNodeExecutor
{
    private readonly CopilotConnectorService _copilotService;

    public CopilotAgentNode(CopilotConnectorService copilotService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _copilotService = copilotService;
    }

    public override string NodeType => "CopilotAgent";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node, 
        Dictionary<string, object?> inputData, 
        string userId)
    {
        var config = JsonSerializer.Deserialize<CopilotAgentConfig>(node.Configuration ?? "{}") 
            ?? new CopilotAgentConfig();
        
        // Preserve original prompt values for restoration
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        // Validate connection
        if (config.ConnectionId == null || config.ConnectionId == Guid.Empty)
        {
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = "No GitHub Copilot connection selected. Please configure a connection in Settings → Connections first." 
            };
        }

        // Get valid Copilot token
        var copilotToken = await _copilotService.GetValidCopilotTokenAsync(config.ConnectionId.Value);
        if (string.IsNullOrEmpty(copilotToken))
        {
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = "Failed to get Copilot API token. The connection may have expired. Please reconnect in Settings → Connections." 
            };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);
        var model = config.Model ?? "gpt-4o";

        // ═══════════════════════════════════════════════════════════════════════════
        // ORCHESTRATOR CONTEXT HANDLING (Ephemeral - from inputData, not persisted)
        // ═══════════════════════════════════════════════════════════════════════════
        
        if (inputData.TryGetValue("_OrchestratorSystemPromptOverride", out var sysOverride) && 
            !string.IsNullOrEmpty(sysOverride?.ToString()))
        {
            systemPrompt = sysOverride.ToString()!;
            Log(node, NodeLogLevel.Info, "Using orchestrator system prompt override");
        }
        
        if (inputData.TryGetValue("_OrchestratorPromptAppend", out var promptAppend) && 
            !string.IsNullOrEmpty(promptAppend?.ToString()))
        {
            prompt = $"{prompt}\n\n[Orchestrator Feedback]:\n{promptAppend}";
            Log(node, NodeLogLevel.Info, $"Appended orchestrator feedback: {promptAppend?.ToString()?.Substring(0, Math.Min(100, promptAppend?.ToString()?.Length ?? 0))}...");
        }
        
        if (inputData.TryGetValue("_OrchestratorIteration", out var iteration))
        {
            var roleName = inputData.GetValueOrDefault("_OrchestratorRoleName")?.ToString() ?? "Agent";
            Log(node, NodeLogLevel.Info, $"Orchestrated execution: Role={roleName}, Iteration={iteration}");
        }
        
        // ═══════════════════════════════════════════════════════════════════════════
        
        // Default system prompt for multi-tool chaining
        if (string.IsNullOrWhiteSpace(systemPrompt) && config.Tools.Count > 0)
        {
            systemPrompt = "You have access to multiple tools. When the task requires multiple steps, call each tool in sequence. After receiving a tool result, continue calling additional tools until the task is complete.";
        }

        if (config.Tools.Count == 0)
        {
            Log(node, NodeLogLevel.Warning, "No tools configured. Connect tool:* labeled connections from the Orchestrator to enable tool calling.");
        }

        // Build initial messages
        var messages = new List<CopilotChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new CopilotChatMessage { Role = "system", Content = systemPrompt });
        }
        messages.Add(new CopilotChatMessage { Role = "user", Content = prompt });

        // Execute with tool calling loop
        return await ExecuteWithToolCallingAsync(node, inputData, config, messages, model, copilotToken, originalPrompt, originalSystemPrompt);
    }

    private async Task<NodeExecutionResult> ExecuteWithToolCallingAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        CopilotAgentConfig config,
        List<CopilotChatMessage> messages,
        string model,
        string copilotToken,
        string originalPrompt,
        string? originalSystemPrompt)
    {
        var toolCallsUsed = 0;
        var allToolResults = new List<object>();
        int totalTokens = 0;

        // Build tools array for Copilot API
        List<CopilotToolDefinition>? tools = null;
        if (config.Tools.Count > 0)
        {
            tools = config.Tools.Select(t => new CopilotToolDefinition
            {
                Type = "function",
                Function = new CopilotFunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = new
                    {
                        type = "object",
                        properties = t.Parameters
                            .Where(p => p.Value.IsEnabled)
                            .ToDictionary(
                                p => p.Key,
                                p => new { type = p.Value.Type, description = p.Value.Description }
                            ),
                        required = t.Required.Where(r => t.Parameters.TryGetValue(r, out var p) && p.IsEnabled).ToList()
                    }
                }
            }).ToList();

            Log(node, NodeLogLevel.Info, $"Agent Mode with {config.Tools.Count} tools: {string.Join(", ", config.Tools.Select(t => t.Name))}");
        }

        for (int iteration = 0; iteration < config.MaxToolCalls; iteration++)
        {
            Log(node, NodeLogLevel.Info, $"Iteration {iteration + 1}/{config.MaxToolCalls}");

            // Build request
            var request = new CopilotChatRequest
            {
                Model = model,
                Messages = messages,
                Tools = tools,
                Stream = false
            };

            // Send request
            var response = await _copilotService.ChatCompletionsAsync(copilotToken, request);
            
            if (response == null || !string.IsNullOrEmpty(response.Error))
            {
                Log(node, NodeLogLevel.Error, $"Copilot API error: {response?.Error ?? "null response"}", response?.ErrorDetails);
                return new NodeExecutionResult 
                { 
                    Success = false, 
                    ErrorMessage = $"Copilot API error: {response?.Error ?? "null response"}" 
                };
            }

            // Track usage
            if (response.Usage != null)
            {
                totalTokens += response.Usage.TotalTokens;
            }

            // Copilot API may return MULTIPLE choices - some with content, some with tool_calls
            // We need to collect tool_calls from ALL choices
            var allToolCalls = new List<CopilotToolCall>();
            string? textContent = null;
            CopilotChatMessage? assistantMessage = null;
            
            if (response.Choices != null)
            {
                foreach (var choice in response.Choices)
                {
                    if (choice.Message == null) continue;
                    
                    // Capture text content from any choice that has it
                    if (!string.IsNullOrEmpty(choice.Message.Content))
                    {
                        textContent = choice.Message.Content;
                        assistantMessage = choice.Message;
                    }
                    
                    // Collect tool_calls from any choice that has them
                    if (choice.Message.ToolCalls != null && choice.Message.ToolCalls.Count > 0)
                    {
                        allToolCalls.AddRange(choice.Message.ToolCalls);
                    }
                }
            }
            
            if (assistantMessage == null && allToolCalls.Count == 0)
            {
                return new NodeExecutionResult 
                { 
                    Success = false, 
                    ErrorMessage = "No response from Copilot API" 
                };
            }

            // Check for tool_calls (collected from ALL choices)
            if (allToolCalls.Count > 0)
            {
                Log(node, NodeLogLevel.Info, $"AI requested {allToolCalls.Count} tool call(s)");

                // Add assistant message with the combined tool_calls to history
                var combinedAssistantMessage = new CopilotChatMessage
                {
                    Role = "assistant",
                    Content = textContent,
                    ToolCalls = allToolCalls
                };
                messages.Add(combinedAssistantMessage);

                foreach (var toolCall in allToolCalls)
                {
                    toolCallsUsed++;
                    var functionName = toolCall.Function?.Name ?? "";
                    var functionArgs = toolCall.Function?.Arguments ?? "{}";

                    Log(node, NodeLogLevel.Info, $"Executing tool: {functionName}", functionArgs);

                    var toolDef = config.Tools.FirstOrDefault(t => t.Name == functionName);
                    var toolResult = toolDef == null
                        ? JsonSerializer.Serialize(new { error = $"Tool '{functionName}' not found" })
                        : await ExecuteToolNodeAsync(node, inputData, toolDef, functionArgs);

                    Log(node, NodeLogLevel.Info, $"Tool result", toolResult.Length > 500 ? toolResult[..500] + "..." : toolResult);

                    messages.Add(new CopilotChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Content = toolResult
                    });

                    allToolResults.Add(new { tool = functionName, result = toolResult });
                }
                continue;
            }

            // No tool calls - AI is done
            var aiResponse = textContent ?? "";

            config.PremiumRequestsUsed += iteration + 1;
            config.Prompt = originalPrompt;
            config.SystemPrompt = originalSystemPrompt;
            node.Configuration = JsonSerializer.Serialize(config);
            _executionManager?.NotifyConfigurationUpdated(node.Id, node.Configuration);

            Log(node, NodeLogLevel.Info, $"Complete. Tool calls: {toolCallsUsed}, Total tokens: {totalTokens}");
            
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
                    { "TotalTokens", totalTokens }
                }
            };
        }

        // Max iterations
        Log(node, NodeLogLevel.Warning, $"Max tool calls ({config.MaxToolCalls}) reached");
        config.Prompt = originalPrompt;
        config.SystemPrompt = originalSystemPrompt;
        node.Configuration = JsonSerializer.Serialize(config);

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
        CopilotAgentToolDefinition toolDef,
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

            // Extract workflow context
            Guid? workflowId = null;
            if (inputData.TryGetValue("_WorkflowId", out var wfIdObj) && wfIdObj is Guid wfId)
            {
                workflowId = wfId;
            }
            var executionService = inputData.GetValueOrDefault("_WorkflowExecutionService");

            if (workflowId == null || executionService == null)
            {
                Log(node, NodeLogLevel.Error, $"Missing workflow context. WorkflowId present: {workflowId != null}, ExecutionService present: {executionService != null}");
                return JsonSerializer.Serialize(new { error = "Workflow context not available" });
            }

            // Apply NodeConfigField mappings
            var mappedOverrides = MapParametersToConfigFields(toolDef, paramOverrides);

            // Try ExecuteToolWithParametersAsync first
            var executeMethod = executionService.GetType().GetMethod("ExecuteToolWithParametersAsync");
            if (executeMethod != null)
            {
                var result = executeMethod.Invoke(executionService, new object?[] { workflowId.Value, toolDef.EntryNodeId, mappedOverrides, inputData });
                if (result is Task<Dictionary<string, object?>> taskWithResult)
                {
                    var outputData = await taskWithResult;
                    
                    if (outputData.TryGetValue("Error", out var error) && error != null)
                    {
                        Log(node, NodeLogLevel.Info, $"Workflow service returned error: {error}, trying direct execution");
                        return await ExecuteToolDirectlyAsync(node, inputData, toolDef, paramOverrides);
                    }
                    
                    return JsonSerializer.Serialize(outputData);
                }
            }

            return await ExecuteToolDirectlyAsync(node, inputData, toolDef, paramOverrides);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Tool error: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> ExecuteToolDirectlyAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        CopilotAgentToolDefinition toolDef,
        Dictionary<string, object?> paramOverrides)
    {
        try
        {
            var factoryObj = inputData.GetValueOrDefault("_ExecutorFactory");
            var canvasNodesObj = inputData.GetValueOrDefault("_CanvasNodes");
            var userId = inputData.GetValueOrDefault("_UserId")?.ToString() ?? "";
            
            if (factoryObj == null || canvasNodesObj == null)
            {
                return JsonSerializer.Serialize(new { error = "Direct execution context not available. Run the full workflow instead." });
            }
            
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
            
            var nodeTypeProperty = toolCanvasNode.GetType().GetProperty("NodeType");
            var nameProperty = toolCanvasNode.GetType().GetProperty("Name");
            var configProperty = toolCanvasNode.GetType().GetProperty("Configuration");
            
            var nodeType = nodeTypeProperty?.GetValue(toolCanvasNode)?.ToString() ?? "";
            var nodeName = nameProperty?.GetValue(toolCanvasNode)?.ToString() ?? "";
            var config = configProperty?.GetValue(toolCanvasNode)?.ToString() ?? "{}";
            
            // Inject parameters into config
            if (paramOverrides.Count > 0)
            {
                try
                {
                    var configDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config) 
                        ?? new Dictionary<string, JsonElement>();
                    
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
                    
                    var dynamicMappings = toolDef.Parameters
                        .Where(p => p.Value.IsEnabled && !string.IsNullOrEmpty(p.Value.NodeConfigField))
                        .ToDictionary(
                            p => p.Key, 
                            p => p.Value.NodeConfigField, 
                            StringComparer.OrdinalIgnoreCase
                        );
                    
                    foreach (var param in paramOverrides)
                    {
                        string configKey = dynamicMappings.TryGetValue(param.Key, out var mapped)
                            ? mapped
                            : ToPascalCase(param.Key);
                        
                        var value = param.Value;
                        if (configKey == "Operation" && value is string opValue)
                        {
                            value = NormalizeOperationValue(opValue);
                        }
                        
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
                    Log(node, NodeLogLevel.Warning, $"Config injection failed: {ex.Message}");
                }
            }
            
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
            
            var workflowNode = new WorkflowNode
            {
                Id = toolDef.EntryNodeId,
                NodeType = nodeType,
                Name = nodeName,
                Configuration = config
            };
            
            var toolInputData = new Dictionary<string, object?>(inputData);
            foreach (var param in paramOverrides)
            {
                toolInputData[param.Key] = param.Value;
            }
            
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

    private Dictionary<string, object?> MapParametersToConfigFields(
        CopilotAgentToolDefinition toolDef,
        Dictionary<string, object?> paramOverrides)
    {
        var mapped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        
        var dynamicMappings = toolDef.Parameters
            .Where(p => p.Value.IsEnabled && !string.IsNullOrEmpty(p.Value.NodeConfigField))
            .ToDictionary(
                p => p.Key, 
                p => p.Value.NodeConfigField, 
                StringComparer.OrdinalIgnoreCase
            );
        
        foreach (var param in paramOverrides)
        {
            string configKey = dynamicMappings.TryGetValue(param.Key, out var dynamicMapped)
                ? dynamicMapped
                : ToPascalCase(param.Key);
            
            var value = param.Value;
            if (configKey.Equals("Operation", StringComparison.OrdinalIgnoreCase) && value is string opStr)
            {
                value = NormalizeOperationValue(opStr);
            }
            
            mapped[configKey] = value;
        }
        
        return mapped;
    }

    private string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        var parts = input.Split('_');
        var result = string.Join("", parts.Select(p => 
            string.IsNullOrEmpty(p) ? "" : char.ToUpper(p[0]) + p.Substring(1).ToLower()));
        
        if (!input.Contains('_') && char.IsLower(input[0]))
            return char.ToUpper(input[0]) + input.Substring(1);
        
        return result;
    }

    private string NormalizeOperationValue(string operation)
    {
        var normalized = operation.ToLowerInvariant().Replace("_", "").Replace("-", "");
        
        return normalized switch
        {
            "write" or "writefile" or "create" or "createfile" or "save" or "savefile" => "Write",
            "read" or "readfile" or "get" or "getfile" or "load" or "loadfile" => "Read",
            "edit" or "editfile" or "update" or "updatefile" or "modify" => "Edit",
            "delete" or "deletefile" or "remove" or "removefile" => "Delete",
            "list" or "listfiles" or "dir" or "ls" => "List",
            "createfolder" or "mkdir" or "makedir" or "newfolder" => "CreateFolder",
            "post" => "POST",
            "put" => "PUT",
            "patch" => "PATCH",
            _ => char.ToUpper(operation[0]) + operation.Substring(1).ToLower()
        };
    }

    public override List<string> GetOutputParameters() => new() 
    { 
        "AIResponse", "ModelUsed", "ToolCallsUsed", "ToolResults", "TotalTokens" 
    };

    private string ReplacePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        foreach (var kvp in data)
            template = template.Replace($"{{{{{kvp.Key}}}}}", kvp.Value?.ToString() ?? "");
        return template;
    }
}

#region Configuration DTOs

public class CopilotAgentConfig
{
    /// <summary>Reference to user's GitHub Copilot OAuth connection</summary>
    public Guid? ConnectionId { get; set; }
    
    public string Model { get; set; } = "gpt-4o";
    public string Prompt { get; set; } = "";
    public string? SystemPrompt { get; set; } = "You have access to multiple tools. When the task requires multiple steps, call each tool in sequence. After receiving a tool result, continue calling additional tools until the task is complete.";
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxToolCalls { get; set; } = 10;
    
    /// <summary>Count of premium requests used (for display)</summary>
    public int PremiumRequestsUsed { get; set; } = 0;
    
    /// <summary>Tool definitions discovered from connections</summary>
    public List<CopilotAgentToolDefinition> Tools { get; set; } = new();
}

public class CopilotAgentToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid EntryNodeId { get; set; }
    public Dictionary<string, CopilotAgentToolParameter> Parameters { get; set; } = new();
    public List<string> Required { get; set; } = new();
    public List<CopilotAgentToolOutput> Outputs { get; set; } = new();
    public bool IsExpanded { get; set; } = false;
}

public class CopilotAgentToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string NodeConfigField { get; set; } = "";
}

public class CopilotAgentToolOutput
{
    public string Placeholder { get; set; } = "";
    public string DataType { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}

#endregion
