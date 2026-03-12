using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;
using System.Linq;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Local LLM Agent Node with function/tool calling support.
/// Connects to self-hosted OpenAI-compatible servers (vLLM, Ollama, etc.)
/// and enables the AI to invoke connected workflow nodes as tools.
/// </summary>
public class LocalLlmAgentNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;

    public LocalLlmAgentNode(HttpClient httpClient, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
    }

    public override string NodeType => "LocalLlmAgent";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<LocalLlmAgentConfig>(node.Configuration ?? "{}") ?? new LocalLlmAgentConfig();

        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        // Validate required fields
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Base URL is required. Configure the URL of your local LLM server."
            };
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Model name is required."
            };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);

        // ═══════════════════════════════════════════════════════════════════════════
        // ORCHESTRATOR CONTEXT HANDLING
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

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = "First list the files, then read the contents of test.txt, then write a summary.";
        }

        if (config.Tools.Count == 0)
        {
            Log(node, NodeLogLevel.Warning, "No tools configured. Connect tool:* labeled connections to enable tool calling.");
        }

        // Build initial messages
        var messages = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new Dictionary<string, object?> { { "role", "system" }, { "content", systemPrompt } });
        }
        messages.Add(new Dictionary<string, object?> { { "role", "user" }, { "content", prompt } });

        return await ExecuteWithToolCallingAsync(node, inputData, config, messages, originalPrompt, originalSystemPrompt);
    }

    private async Task<NodeExecutionResult> ExecuteWithToolCallingAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        LocalLlmAgentConfig config,
        List<Dictionary<string, object?>> messages,
        string originalPrompt,
        string? originalSystemPrompt)
    {
        var toolCallsUsed = 0;
        var allToolResults = new List<object>();
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
                            .Where(p => p.Value.IsEnabled)
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

        // Construct API URL
        var baseUrl = config.BaseUrl.TrimEnd('/');
        var apiUrl = baseUrl.EndsWith("/v1")
            ? $"{baseUrl}/chat/completions"
            : baseUrl.Contains("/chat/completions")
                ? baseUrl
                : $"{baseUrl}/v1/chat/completions";

        for (int iter = 0; iter < config.MaxToolCalls; iter++)
        {
            Log(node, NodeLogLevel.Info, $"Iteration {iter + 1}/{config.MaxToolCalls}");

            // Build request body
            var requestBody = new Dictionary<string, object?>
            {
                { "model", config.Model },
                { "messages", messages.ToArray() },
                { "stream", false }
            };

            if (tools != null)
            {
                requestBody["tools"] = tools;
            }

            if (config.EnableThinking)
            {
                requestBody["chat_template_kwargs"] = new Dictionary<string, object>
                {
                    { "enable_thinking", true }
                };
            }

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = JsonContent.Create(requestBody)
            };

            if (!string.IsNullOrEmpty(config.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
            }

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
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"LLM API error ({(int)response.StatusCode}): {Truncate(responseContent, 500)}"
                };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

            // Track usage
            if (jsonResponse.TryGetProperty("usage", out var usage))
            {
                long inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt64() : 0;
                long outputTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt64() : 0;
                totalInputTokens += inputTokens;
                totalOutputTokens += outputTokens;
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

            // No tool calls — AI is done
            var rawContent = message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";

            // Parse thinking tags
            string aiResponse;
            string thinkingContent = "";

            if (config.EnableThinking || rawContent.Contains("<think>"))
            {
                var thinkMatch = Regex.Match(rawContent, @"<think>(.*?)</think>", RegexOptions.Singleline);
                if (thinkMatch.Success)
                {
                    thinkingContent = thinkMatch.Groups[1].Value.Trim();
                    aiResponse = Regex.Replace(rawContent, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
                    Log(node, NodeLogLevel.Info, $"Extracted thinking content ({thinkingContent.Length} chars)");
                }
                else
                {
                    aiResponse = rawContent;
                }
            }
            else
            {
                aiResponse = rawContent;
            }

            UpdateConfigWithStats(node, config, totalInputTokens, totalOutputTokens, originalPrompt, originalSystemPrompt);

            Log(node, NodeLogLevel.Info, $"Complete. Tool calls: {toolCallsUsed}, Tokens: {totalInputTokens} in / {totalOutputTokens} out");
            Log(node, NodeLogLevel.Info, "AIResponse content", Truncate(aiResponse, 500));

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AIResponse", aiResponse },
                    { "ThinkingContent", thinkingContent },
                    { "ModelUsed", config.Model },
                    { "ToolCallsUsed", toolCallsUsed },
                    { "ToolResults", allToolResults }
                }
            };
        }

        // Max iterations reached
        Log(node, NodeLogLevel.Warning, $"Max tool calls ({config.MaxToolCalls}) reached");
        UpdateConfigWithStats(node, config, totalInputTokens, totalOutputTokens, originalPrompt, originalSystemPrompt);

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "AIResponse", "Max tool calls reached" },
                { "ModelUsed", config.Model },
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

            // Extract workflow context
            Guid? workflowId = null;
            if (inputData.TryGetValue("_WorkflowId", out var wfIdObj) && wfIdObj is Guid wfId)
            {
                workflowId = wfId;
            }
            var executionService = inputData.GetValueOrDefault("_WorkflowExecutionService");

            Log(node, NodeLogLevel.Info, $"Workflow context: WorkflowId={workflowId}, ExecutionService={executionService != null}");

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

            // Fallback to direct execution
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
        DeepSeekAgentToolDefinition toolDef,
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
                        if (dynamicMappings.TryGetValue(param.Key, out var mapped))
                        {
                            configKey = mapped;
                        }
                        else if (fallbackMappings.TryGetValue(param.Key, out var fallbackMapped))
                        {
                            configKey = fallbackMapped;
                        }
                        else
                        {
                            configKey = ToPascalCase(param.Key);
                        }

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
                    Log(node, NodeLogLevel.Warning, $"Config injection failed, using placeholder fallback: {ex.Message}");
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

    private void UpdateConfigWithStats(WorkflowNode node, LocalLlmAgentConfig config, long input, long output, string origPrompt, string? origSystem)
    {
        config.InputTokens += input;
        config.OutputTokens += output;
        config.Prompt = origPrompt;
        config.SystemPrompt = origSystem;
        node.Configuration = JsonSerializer.Serialize(config);
        _executionManager?.NotifyConfigurationUpdated(node.Id, node.Configuration);
    }

    public override List<string> GetOutputParameters() => new()
    {
        "AIResponse", "ThinkingContent", "ModelUsed", "ToolCallsUsed", "ToolResults"
    };

    private string ReplacePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        foreach (var kvp in data)
        {
            if (kvp.Key.StartsWith("_")) continue;
            template = template.Replace($"{{{{{kvp.Key}}}}}", kvp.Value?.ToString() ?? "");
        }
        return template;
    }

    private Dictionary<string, object?> MapParametersToConfigFields(
        DeepSeekAgentToolDefinition toolDef,
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

        var fallbackMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "operation", "Operation" },
            { "path", "FilePath" },
            { "filepath", "FilePath" },
            { "file_path", "FilePath" },
            { "content", "Content" },
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
                configKey = ToPascalCase(param.Key);
            }

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

    private static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }
}

/// <summary>
/// Configuration model for the Local LLM Agent node.
/// </summary>
public class LocalLlmAgentConfig
{
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? SystemPrompt { get; set; } = "You have access to multiple tools. When the task requires multiple steps, call each tool in sequence. After receiving a tool result, continue calling additional tools until the task is complete.";
    public bool EnableThinking { get; set; } = false;
    public double Temperature { get; set; } = 0.7;
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxToolCalls { get; set; } = 10;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;

    /// <summary>Tool definitions — reuses DeepSeekAgentToolDefinition type</summary>
    public List<DeepSeekAgentToolDefinition> Tools { get; set; } = new();
}
