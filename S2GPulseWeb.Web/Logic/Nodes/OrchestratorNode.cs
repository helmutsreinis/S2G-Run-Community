using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Orchestrator node that coordinates multi-agent AI workflows with iterative refinement.
/// Manages agent roles, tool chain execution, and success evaluation.
/// </summary>
public class OrchestratorNode : BaseNodeExecutor
{
    // Track executing orchestrators to prevent concurrent execution of the same node
    private static readonly ConcurrentDictionary<Guid, bool> _executingOrchestrators = new();

    public OrchestratorNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Orchestrator";

    public override List<string> GetOutputParameters() => new()
    {
        "FinalResult", "IterationCount", "IsSuccess", "AgentResponses",
        "LastEvaluation", "ExecutionLog", "TotalToolCalls"
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node, 
        Dictionary<string, object?> inputData, 
        string userId)
    {
        // Execution lock: prevent concurrent execution of the same orchestrator
        if (!_executingOrchestrators.TryAdd(node.Id, true))
        {
            Log(node, NodeLogLevel.Warning, "Orchestrator already executing", 
                "Skipping - this orchestrator is already running. Wait for it to complete.");
            return new NodeExecutionResult
            {
                Success = false,
                OutputData = new Dictionary<string, object?>
                {
                    { "FinalResult", "already_executing" },
                    { "IsSuccess", false }
                }
            };
        }

        try
        {
            var config = JsonSerializer.Deserialize<OrchestratorConfig>(node.Configuration ?? "{}") 
                ?? new OrchestratorConfig();

            Log(node, NodeLogLevel.Info, "Starting orchestration", 
                $"Task: {Truncate(config.TaskDescription, 100)}, Max Iterations: {config.MaxIterations}");

            // Initialize execution state
            var executionState = new OrchestratorExecutionState
            {
            StartedAt = DateTime.UtcNow,
            MaxIterations = config.MaxIterations
        };

        // Discover connected agents and tools from workflow connections
        var connectedAgents = DiscoverConnectedAgents(node, config);
        var connectedTools = DiscoverConnectedTools(node, config);

        Log(node, NodeLogLevel.Info, "Discovered connections", 
            $"Agents: {connectedAgents.Count}, Tool Chains: {connectedTools.Count}");
        
        // Log agent names for debugging
        if (connectedAgents.Count > 0)
        {
            Log(node, NodeLogLevel.Info, "Agent roster", 
                string.Join(", ", connectedAgents.Select(a => $"'{a.RoleName}'")));
        }

        // Early exit if no agents are connected
        if (connectedAgents.Count == 0)
        {
            Log(node, NodeLogLevel.Warning, "No agents connected", 
                "Cannot orchestrate without connected AI agents. Draw connections from AI nodes to this Orchestrator with 'agent' label.");
            
            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "FinalResult", "no_agents_connected" },
                    { "IterationCount", 0 },
                    { "IsSuccess", false },
                    { "AgentResponses", "{}" },
                    { "LastEvaluation", "No agents connected to orchestrator" },
                    { "_TriggeredTags", "[\"error\"]" }
                }
            };
        }

        bool success = false;
        string lastEvaluation = "";
        var agentResponses = new Dictionary<string, object?>();
        var lastAgentResponses = new Dictionary<string, string>();  // Track per-agent responses for iteration

        // Check if steering AI is configured
        var hasSteeringAI = config.SteeringAINodeId.HasValue && config.SteeringAINodeId != Guid.Empty;

        if (hasSteeringAI)
        {
            // ═══════════════════════════════════════════════════════════════════════
            // STEERING AI MODE: AI-driven routing decisions
            // ═══════════════════════════════════════════════════════════════════════
            Log(node, NodeLogLevel.Info, "Steering AI mode enabled", 
                $"Steering AI: {config.SteeringAINodeName ?? config.SteeringAINodeId.ToString()}");
            
            // Generate system prompt for steering AI with agent roster
            var steeringSystemPrompt = GenerateSteeringSystemPrompt(
                config.TaskDescription, connectedAgents, config.SteeringAIPromptPrefix);
            
            for (int iteration = 1; iteration <= config.MaxIterations && !success; iteration++)
            {
                await Task.Yield();
                executionState.CurrentIteration = iteration;
                Log(node, NodeLogLevel.Info, $"Steering iteration {iteration}/{config.MaxIterations}");

                // Build context for steering AI call
                var steeringContext = new Dictionary<string, object?>
                {
                    ["SteeringSystemPrompt"] = steeringSystemPrompt,
                    ["CurrentTaskContext"] = BuildSteeringContext(iteration, agentResponses, lastEvaluation),
                    ["Iteration"] = iteration,
                    ["MaxIterations"] = config.MaxIterations
                };

                // Call steering AI
                var steeringResponse = await ExecuteSteeringAIAsync(
                    config.SteeringAINodeId!.Value, steeringContext, inputData);
                
                Log(node, NodeLogLevel.Info, "Steering AI response", 
                    Truncate(steeringResponse, 300));

                // Parse steering decision
                var decision = ParseSteeringDecision(steeringResponse);
                Log(node, NodeLogLevel.Info, $"Steering decision: {decision.Action}", 
                    decision.AgentName ?? decision.Summary ?? decision.Reason ?? "");

                if (decision.Action == "complete")
                {
                    success = true;
                    lastEvaluation = decision.Summary ?? "Task completed successfully";
                    Log(node, NodeLogLevel.Info, "Task marked complete by steering AI");
                    break;
                }
                
                if (decision.Action == "error")
                {
                    lastEvaluation = decision.Reason ?? "Error from steering AI";
                    Log(node, NodeLogLevel.Warning, "Steering AI returned error", lastEvaluation);
                    break;
                }

                if (decision.Action == "call_agent")
                {
                    var targetAgent = connectedAgents.FirstOrDefault(a => 
                        string.Equals(a.RoleName, decision.AgentName, StringComparison.OrdinalIgnoreCase));
                    
                    if (targetAgent == null)
                    {
                        Log(node, NodeLogLevel.Warning, $"Agent not found: {decision.AgentName}");
                        lastEvaluation = $"Steering AI requested unknown agent: {decision.AgentName}";
                        continue;
                    }

                    Log(node, NodeLogLevel.Info, $"Calling agent: {targetAgent.RoleName}", 
                        $"Prompt append: {Truncate(decision.PromptAppend, 100)}");

                    // Build context and execute agent
                    var agentContext = BuildAgentContext(config, targetAgent, agentResponses, lastEvaluation, iteration);
                    var previousResponse = lastAgentResponses.GetValueOrDefault(targetAgent.RoleName);
                    
                    try
                    {
                        var agentResult = await TriggerAgentExecutionAsync(
                            targetAgent, agentContext, inputData, 
                            decision.PromptAppend, previousResponse);
                        
                        // Store the AIResponse for steering AI evaluation
                        var aiResponse = agentResult.GetValueOrDefault("AIResponse")?.ToString() ?? "";
                        agentResponses[targetAgent.RoleName] = agentResult;
                        lastAgentResponses[targetAgent.RoleName] = aiResponse;
                        
                        executionState.AgentExecutions.Add(new AgentExecutionRecord
                        {
                            RoleName = targetAgent.RoleName,
                            NodeId = targetAgent.NodeId,
                            Iteration = iteration,
                            Response = aiResponse,
                            ExecutedAt = DateTime.UtcNow
                        });
                    }
                    catch (Exception ex)
                    {
                        Log(node, NodeLogLevel.Error, $"Agent execution failed: {targetAgent.RoleName}", ex.Message);
                        executionState.Errors.Add($"Iteration {iteration}, Agent {targetAgent.RoleName}: {ex.Message}");
                    }
                }
            }
        }
        else
        {
            // ═══════════════════════════════════════════════════════════════════════
            // LEGACY MODE: Sequential agent execution without steering AI
            // ═══════════════════════════════════════════════════════════════════════
            Log(node, NodeLogLevel.Info, "Sequential mode (no steering AI)");
            
            for (int iteration = 1; iteration <= config.MaxIterations && !success; iteration++)
            {
                await Task.Yield();
                executionState.CurrentIteration = iteration;
                Log(node, NodeLogLevel.Info, $"Starting iteration {iteration}/{config.MaxIterations}");

                // Execute agents in order
                foreach (var agent in connectedAgents.OrderBy(a => a.ExecutionOrder))
                {
                    Log(node, NodeLogLevel.Info, $"Executing agent: {agent.RoleName}", 
                        $"Node: {agent.NodeId}, Type: {agent.NodeType}");

                    try
                    {
                        var agentContext = BuildAgentContext(config, agent, agentResponses, lastEvaluation, iteration);
                        
                        // Execute agent's assigned tool chains if any
                        foreach (var toolTag in agent.AssignedTools)
                        {
                            var toolChain = connectedTools.FirstOrDefault(t => t.ToolTag == toolTag);
                            if (toolChain != null)
                            {
                                Log(node, NodeLogLevel.Info, $"Executing tool chain: {toolTag}");
                                var toolResult = await ExecuteToolChainAsync(toolChain, inputData, userId);
                                executionState.TotalToolCalls++;
                                
                                foreach (var kvp in toolResult)
                                {
                                    inputData[$"{toolTag}.{kvp.Key}"] = kvp.Value;
                                }
                            }
                        }

                        var agentResult = await TriggerAgentExecutionAsync(agent, agentContext, inputData);
                        var aiResponse = agentResult.GetValueOrDefault("AIResponse")?.ToString() ?? "";
                        agentResponses[agent.RoleName] = agentResult;
                        
                        executionState.AgentExecutions.Add(new AgentExecutionRecord
                        {
                            RoleName = agent.RoleName,
                            NodeId = agent.NodeId,
                            Iteration = iteration,
                            Response = aiResponse,
                            ExecutedAt = DateTime.UtcNow
                        });
                    }
                    catch (Exception ex)
                    {
                        Log(node, NodeLogLevel.Error, $"Agent execution failed: {agent.RoleName}", ex.Message);
                        executionState.Errors.Add($"Iteration {iteration}, Agent {agent.RoleName}: {ex.Message}");
                    }
                }

                // Evaluate success based on configured criteria
                (success, lastEvaluation) = await EvaluateSuccessAsync(config, agentResponses, inputData, iteration);
                
                Log(node, NodeLogLevel.Info, $"Iteration {iteration} evaluation", 
                    $"Success: {success}, Evaluation: {Truncate(lastEvaluation, 200)}");

                if (!success && iteration < config.MaxIterations)
                {
                    Log(node, NodeLogLevel.Info, "Preparing for next iteration with feedback");
                }
            }
        }

        executionState.CompletedAt = DateTime.UtcNow;
        executionState.IsSuccess = success;
        executionState.FinalEvaluation = lastEvaluation;

        // Update config with execution history
        config.LastExecutedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        config.LastIterationCount = executionState.CurrentIteration;
        config.LastSuccess = success;
        node.Configuration = JsonSerializer.Serialize(config);

        // Determine which output tag to trigger
        var triggeredTags = new List<string> { success ? "complete" : "error" };

        Log(node, NodeLogLevel.Info, "Orchestration complete", 
            $"Iterations: {executionState.CurrentIteration}, Success: {success}, Triggering: {triggeredTags[0]}");

        // Use serializer options that handle circular references
        var jsonOptions = new JsonSerializerOptions
        {
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            WriteIndented = false
        };

        return new NodeExecutionResult
        {
            Success = true, // Orchestrator succeeded even if evaluation failed
            OutputData = new Dictionary<string, object?>
            {
                { "FinalResult", success ? "completed" : "max_iterations_reached" },
                { "IterationCount", executionState.CurrentIteration },
                { "IsSuccess", success },
                { "AgentResponses", JsonSerializer.Serialize(agentResponses, jsonOptions) },
                { "LastEvaluation", lastEvaluation },
                { "ExecutionLog", JsonSerializer.Serialize(executionState.AgentExecutions, jsonOptions) },
                { "TotalToolCalls", executionState.TotalToolCalls },
                { "_TriggeredTags", JsonSerializer.Serialize(triggeredTags) }
            }
        };
        }
        finally
        {
            // Release execution lock so this orchestrator can be triggered again
            _executingOrchestrators.TryRemove(node.Id, out _);
        }
    }

    /// <summary>
    /// Discovers agents connected to the orchestrator via "agent" labeled connections.
    /// Currently reads from config; auto-discovery from connections happens in editor.
    /// </summary>
    private List<ConnectedAgent> DiscoverConnectedAgents(WorkflowNode node, OrchestratorConfig config)
    {
        var agents = new List<ConnectedAgent>();
        
        // Use registered agents from config (discovered by editor from canvas connections)
        foreach (var agentConfig in config.Agents.Where(a => a.IsEnabled))
        {
            agents.Add(new ConnectedAgent
            {
                NodeId = agentConfig.NodeId,
                RoleName = agentConfig.RoleName,
                NodeType = agentConfig.NodeType ?? "Unknown",
                SkillDescription = agentConfig.SkillDescription,
                SystemPrompt = agentConfig.SystemPrompt,
                AssignedTools = agentConfig.AssignedTools ?? new List<string>(),
                ExecutionOrder = agentConfig.ExecutionOrder
            });
        }
        
        return agents;
    }

    /// <summary>
    /// Discovers tool chains connected to the orchestrator via "tool:*" labeled connections.
    /// </summary>
    private List<ToolChainInfo> DiscoverConnectedTools(WorkflowNode node, OrchestratorConfig config)
    {
        var tools = new List<ToolChainInfo>();
        
        // For now, use the registered tools from config
        // In Phase 2, we'll auto-discover from workflow connections with BFS traversal
        foreach (var toolConfig in config.ToolChains)
        {
            tools.Add(new ToolChainInfo
            {
                ToolTag = toolConfig.ToolTag,
                EntryNodeId = toolConfig.EntryNodeId,
                EntryNodeType = toolConfig.EntryNodeType ?? "Unknown",
                BranchCount = toolConfig.BranchCount,
                TotalNodeCount = toolConfig.TotalNodeCount,
                TimeoutSeconds = toolConfig.TimeoutSeconds ?? 60
            });
        }
        
        return tools;
    }

    /// <summary>
    /// Builds the context object passed to an agent for execution.
    /// </summary>
    private Dictionary<string, object?> BuildAgentContext(
        OrchestratorConfig config,
        ConnectedAgent agent,
        Dictionary<string, object?> previousResponses,
        string lastEvaluation,
        int iteration)
    {
        return new Dictionary<string, object?>
        {
            { "TaskDescription", config.TaskDescription },
            { "RoleName", agent.RoleName },
            { "SystemPrompt", agent.SystemPrompt },
            { "Iteration", iteration },
            { "MaxIterations", config.MaxIterations },
            { "IsFirstIteration", iteration == 1 },
            { "PreviousResponses", previousResponses },
            { "LastEvaluation", lastEvaluation },
            { "AssignedTools", agent.AssignedTools }
        };
    }

    /// <summary>
    /// Builds the context string for steering AI, summarizing recent agent responses.
    /// </summary>
    private string BuildSteeringContext(
        int iteration,
        Dictionary<string, object?> agentResponses,
        string lastEvaluation)
    {
        var sb = new System.Text.StringBuilder();
        
        if (iteration == 1 && agentResponses.Count == 0)
        {
            sb.AppendLine("This is the first iteration. No agents have been called yet.");
            sb.AppendLine("Analyze the task and decide which agent should start.");
        }
        else
        {
            sb.AppendLine($"Current iteration: {iteration}");
            
            if (agentResponses.Count > 0)
            {
                sb.AppendLine("\n=== Recent Agent Responses ===");
                foreach (var (agentName, response) in agentResponses)
                {
                    var aiResponse = "";
                    if (response is Dictionary<string, object?> dict)
                    {
                        aiResponse = dict.GetValueOrDefault("AIResponse")?.ToString() ?? "";
                    }
                    else
                    {
                        aiResponse = response?.ToString() ?? "";
                    }
                    
                    // Truncate long responses
                    if (aiResponse.Length > 500)
                    {
                        aiResponse = aiResponse.Substring(0, 500) + "... [truncated]";
                    }
                    
                    sb.AppendLine($"\n[{agentName}]:");
                    sb.AppendLine(aiResponse);
                }
            }
            
            if (!string.IsNullOrEmpty(lastEvaluation))
            {
                sb.AppendLine($"\n=== Last Evaluation ===\n{lastEvaluation}");
            }
            
            sb.AppendLine("\nBased on the above, decide the next action.");
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Executes a tool chain by triggering the entry node.
    /// Uses ExecuteNodeByIdAsync for proper synchronous execution within the workflow.
    /// </summary>
    private async Task<Dictionary<string, object?>> ExecuteToolChainAsync(
        ToolChainInfo toolChain,
        Dictionary<string, object?> inputData,
        string userId)
    {
        // Get workflow context from inputData (injected by WorkflowExecutionService)
        var workflowId = inputData.GetValueOrDefault("_WorkflowId") as Guid?;
        var executionService = inputData.GetValueOrDefault("_WorkflowExecutionService") as object;
        
        // Build input data for the tool chain entry node
        var toolInputData = new Dictionary<string, object?>(inputData)
        {
            ["_OrchestratorToolTag"] = toolChain.ToolTag,
            ["_OrchestratorToolChainId"] = toolChain.EntryNodeId
        };
        
        if (workflowId == null || executionService == null)
        {
            // Fallback to fire-and-forget if workflow context not available
            _executionManager?.TriggerNodeExecution(toolChain.EntryNodeId, toolInputData);
            await Task.Delay(100);
            return new Dictionary<string, object?>
            {
                { "ChainTriggered", true },
                { "ToolTag", toolChain.ToolTag },
                { "Status", "triggered_fallback" }
            };
        }
        
        // Use reflection to call ExecuteNodeByIdAsync
        var executeMethod = executionService.GetType().GetMethod("ExecuteNodeByIdAsync");
        if (executeMethod != null)
        {
            try
            {
                var task = executeMethod.Invoke(executionService, new object?[] { workflowId.Value, toolChain.EntryNodeId, toolInputData }) as Task;
                if (task != null)
                {
                    await task;
                }
                
                return new Dictionary<string, object?>
                {
                    { "ChainExecuted", true },
                    { "ToolTag", toolChain.ToolTag },
                    { "EntryNodeId", toolChain.EntryNodeId },
                    { "Status", "executed" }
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?>
                {
                    { "ChainExecuted", false },
                    { "ToolTag", toolChain.ToolTag },
                    { "Error", ex.InnerException?.Message ?? ex.Message },
                    { "Status", "error" }
                };
            }
        }
        
        return new Dictionary<string, object?>
        {
            { "ToolTag", toolChain.ToolTag },
            { "Status", "execute_method_not_found" }
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // STEERING AI METHODS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates the system prompt for the steering AI that lists available agents and expected response format.
    /// </summary>
    private string GenerateSteeringSystemPrompt(string taskDescription, List<ConnectedAgent> agents, string? customPrefix = null)
    {
        var agentList = string.Join("\n", agents.Select(a => 
            $"- **{a.RoleName}** ({a.NodeType}): {(string.IsNullOrEmpty(a.SkillDescription) ? "No skill description provided" : a.SkillDescription)}"));
        
        var prefix = string.IsNullOrEmpty(customPrefix) ? "" : customPrefix + "\n\n";
        
        return prefix + $$"""
You are a ROUTING COORDINATOR that delegates tasks to specialized agents. Respond ONLY with valid JSON.

TASK TO DELEGATE: {{taskDescription}}

AVAILABLE AGENTS:
{{agentList}}

EXAMPLE RESPONSE (use this exact format):
{"action": "call_agent", "agent": "AgentName", "prompt_append": "Please complete the task with these specific instructions..."}

JSON RESPONSE SCHEMA:
- action: "call_agent" | "complete" | "error"
- agent: exact name from list above (only for call_agent)
- prompt_append: instructions for the agent (only for call_agent)
- summary: completion message (only for complete)
- reason: error explanation (only for error)

Respond with a single JSON object. Do not include any text outside the JSON.
""";
    }

    /// <summary>
    /// Parses the steering AI's JSON response into a decision object.
    /// </summary>
    private SteeringDecision ParseSteeringDecision(string? aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
        {
            return new SteeringDecision { Action = "error", Reason = "Empty response from steering AI" };
        }

        try
        {
            // Try to extract JSON from potential markdown code blocks
            var jsonContent = aiResponse;
            if (aiResponse.Contains("```"))
            {
                var startIdx = aiResponse.IndexOf('{');
                var endIdx = aiResponse.LastIndexOf('}');
                if (startIdx >= 0 && endIdx > startIdx)
                {
                    jsonContent = aiResponse.Substring(startIdx, endIdx - startIdx + 1);
                }
            }

            var json = JsonSerializer.Deserialize<JsonElement>(jsonContent);
            
            var action = json.TryGetProperty("action", out var actionProp) 
                ? actionProp.GetString() ?? "error" 
                : "error";

            return new SteeringDecision
            {
                Action = action,
                AgentName = json.TryGetProperty("agent", out var agentProp) ? agentProp.GetString() : null,
                PromptAppend = json.TryGetProperty("prompt_append", out var promptProp) ? promptProp.GetString() : null,
                Summary = json.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : null,
                Reason = json.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null
            };
        }
        catch (Exception ex)
        {
            return new SteeringDecision 
            { 
                Action = "error", 
                Reason = $"Failed to parse AI response: {ex.Message}. Response was: {Truncate(aiResponse, 200)}" 
            };
        }
    }

    /// <summary>
    /// Executes the steering AI node to get the next routing decision.
    /// </summary>
    private async Task<string?> ExecuteSteeringAIAsync(
        Guid steeringAINodeId,
        Dictionary<string, object?> steeringContext,
        Dictionary<string, object?> inputData)
    {
        // Get workflow context
        Guid? workflowId = null;
        if (inputData.TryGetValue("_WorkflowId", out var wfIdObj) && wfIdObj is Guid wfId)
        {
            workflowId = wfId;
        }
        var executionService = inputData.GetValueOrDefault("_WorkflowExecutionService") as object;

        if (workflowId == null || executionService == null)
        {
            return null;
        }

        // Build steering input with generated system prompt and context
        var steeringInputData = new Dictionary<string, object?>(inputData)
        {
            ["_OrchestratorSteeringContext"] = steeringContext,
            ["_OrchestratorSystemPromptOverride"] = steeringContext.GetValueOrDefault("SteeringSystemPrompt"),
            ["_OrchestratorPromptAppend"] = steeringContext.GetValueOrDefault("CurrentTaskContext")
        };

        // Use ExecuteNodeAndGetOutputAsync to capture steering AI response
        var executeMethod = executionService.GetType().GetMethod("ExecuteNodeAndGetOutputAsync");
        if (executeMethod != null)
        {
            try
            {
                var result = executeMethod.Invoke(executionService, new object?[] { workflowId.Value, steeringAINodeId, steeringInputData });
                
                // Handle async result - must await the Task properly
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);  // CRITICAL: Actually await the task
                    
                    // Get the Result property from Task<T> via reflection
                    var resultProperty = task.GetType().GetProperty("Result");
                    if (resultProperty != null)
                    {
                        var outputData = resultProperty.GetValue(task) as Dictionary<string, object?>;
                        return outputData?.GetValueOrDefault("AIResponse")?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OrchestratorNode] Steering AI execution failed: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Steering AI decision result.
    /// </summary>
    private class SteeringDecision
    {
        public string Action { get; set; } = "error";  // "call_agent", "complete", "error"
        public string? AgentName { get; set; }
        public string? PromptAppend { get; set; }
        public string? Summary { get; set; }
        public string? Reason { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Triggers an agent AI node execution with the provided context.
    /// Uses ExecuteNodeAndGetOutputAsync for synchronous execution with response capture.
    /// </summary>
    private async Task<Dictionary<string, object?>> TriggerAgentExecutionAsync(
        ConnectedAgent agent,
        Dictionary<string, object?> context,
        Dictionary<string, object?> inputData,
        string? promptAppend = null,
        string? previousResponse = null)
    {
        // Get workflow context from inputData (injected by WorkflowExecutionService)
        Guid? workflowId = null;
        if (inputData.TryGetValue("_WorkflowId", out var wfIdObj) && wfIdObj is Guid wfId)
        {
            workflowId = wfId;
        }
        var executionService = inputData.GetValueOrDefault("_WorkflowExecutionService") as object;
        
        if (workflowId == null || executionService == null)
        {
            // Fallback to fire-and-forget if workflow context not available
            Log(GetFakeNode(agent.NodeId), NodeLogLevel.Warning, 
                $"Workflow context not available - using fire-and-forget for agent {agent.RoleName}");
            _executionManager?.TriggerNodeExecution(agent.NodeId, inputData);
            await Task.Delay(100);
            return new Dictionary<string, object?> { { "Status", "triggered_fallback" } };
        }
        
        // Build the input data for the agent node with orchestration context
        // Note: prompt append is EPHEMERAL - fresh each call, not accumulated
        var agentInputData = new Dictionary<string, object?>(inputData)
        {
            ["_OrchestratorContext"] = context,
            ["_OrchestratorTaskDescription"] = context.GetValueOrDefault("TaskDescription"),
            ["_OrchestratorIteration"] = context.GetValueOrDefault("Iteration"),
            ["_OrchestratorRoleName"] = agent.RoleName,
            ["_OrchestratorSystemPromptOverride"] = agent.SystemPrompt,
            ["_OrchestratorPromptAppend"] = promptAppend ?? "",
            ["_OrchestratorPreviousAgentResponse"] = previousResponse ?? ""
        };
        
        // Use ExecuteNodeAndGetOutputAsync to capture the full response after completion
        var executeMethod = executionService.GetType().GetMethod("ExecuteNodeAndGetOutputAsync");
        if (executeMethod != null)
        {
            try
            {
                var result = executeMethod.Invoke(executionService, new object?[] { workflowId.Value, agent.NodeId, agentInputData });
                
                // Handle async result - must await the Task properly
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);  // CRITICAL: Actually await the task
                    
                    // Get the Result property from Task<T> via reflection
                    var resultProperty = task.GetType().GetProperty("Result");
                    if (resultProperty != null)
                    {
                        var outputData = resultProperty.GetValue(task) as Dictionary<string, object?>;
                        if (outputData != null)
                        {
                            // Extract AIResponse from agent output
                            var aiResponse = outputData.GetValueOrDefault("AIResponse")?.ToString() ?? "";
                            Log(GetFakeNode(agent.NodeId), NodeLogLevel.Info, 
                                $"Agent {agent.RoleName} response captured", 
                                aiResponse.Length > 200 ? aiResponse.Substring(0, 200) + "..." : aiResponse);
                            
                            return outputData;
                        }
                    }
                }
                
                Log(GetFakeNode(agent.NodeId), NodeLogLevel.Warning, 
                    $"Agent {agent.RoleName}: ExecuteNodeAndGetOutputAsync returned unexpected type");
            }
            catch (Exception ex)
            {
                Log(GetFakeNode(agent.NodeId), NodeLogLevel.Error, 
                    $"Agent {agent.RoleName} execution failed: {ex.InnerException?.Message ?? ex.Message}");
                return new Dictionary<string, object?>
                {
                    { "Status", "error" },
                    { "Error", ex.InnerException?.Message ?? ex.Message }
                };
            }
        }
        
        // Method not found fallback
        return new Dictionary<string, object?> { { "Status", "execute_method_not_found" } };
    }
    
    // Helper to create a fake WorkflowNode for logging when we only have a NodeId
    private static WorkflowNode GetFakeNode(Guid nodeId) => new WorkflowNode { Id = nodeId, Name = "Agent" };

    /// <summary>
    /// Evaluates whether the orchestration has succeeded based on configured criteria.
    /// </summary>
    private async Task<(bool Success, string Evaluation)> EvaluateSuccessAsync(
        OrchestratorConfig config,
        Dictionary<string, object?> agentResponses,
        Dictionary<string, object?> inputData,
        int iteration)
    {
        if (config.EvaluationType == EvaluationType.Expression)
        {
            // Simple expression evaluation
            var expression = ResolvePlaceholders(config.SuccessCriteria ?? "", inputData, agentResponses);
            var success = EvaluateExpression(expression);
            return (success, $"Expression '{config.SuccessCriteria}' evaluated to: {success}");
        }
        else // AI Evaluation
        {
            // TODO: Implement AI-powered evaluation in Phase 3
            await Task.Delay(10); // Placeholder
            return (false, "AI evaluation not yet implemented");
        }
    }

    /// <summary>
    /// Evaluates a simple boolean expression.
    /// </summary>
    private bool EvaluateExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        // Simple evaluation for common patterns
        var trimmed = expression.Trim().ToLower();
        
        if (trimmed == "true") return true;
        if (trimmed == "false") return false;
        if (trimmed == "success") return true;
        if (trimmed == "error" || trimmed == "failed") return false;
        
        // Check for equality expressions like "status == 'success'"
        var equalMatch = Regex.Match(expression, @"['\""]?(\w+)['\""]?\s*==\s*['\""](\w+)['\""]", RegexOptions.IgnoreCase);
        if (equalMatch.Success)
        {
            var left = equalMatch.Groups[1].Value.ToLower();
            var right = equalMatch.Groups[2].Value.ToLower();
            return left == right;
        }
        
        return false;
    }

    /// <summary>
    /// Resolves placeholders in a template string using both input data and agent responses.
    /// </summary>
    private string ResolvePlaceholders(
        string template, 
        Dictionary<string, object?> inputData,
        Dictionary<string, object?> agentResponses)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var result = template;
        var placeholderRegex = new Regex(@"\{\{([^}]+)\}\}");
        
        result = placeholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            
            // Try input data first
            if (inputData.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? "";
            
            // Try agent responses
            var parts = key.Split('.');
            if (parts.Length >= 2 && agentResponses.TryGetValue(parts[0], out var agentResponse))
            {
                // Try to extract nested property
                if (agentResponse is JsonElement json)
                {
                    try
                    {
                        var current = json;
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (current.TryGetProperty(parts[i], out var next))
                                current = next;
                            else
                                return match.Value;
                        }
                        return current.ToString();
                    }
                    catch { }
                }
            }
            
            return match.Value; // Return original if not resolved
        });

        return result;
    }

    private string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}

#region Configuration Models

/// <summary>
/// Configuration for the Orchestrator node.
/// </summary>
public class OrchestratorConfig
{
    // Core Settings
    public string TaskDescription { get; set; } = "";
    public int MaxIterations { get; set; } = 5;
    
    // Steering AI (auto-detected from 'orchestrate' connection)
    public Guid? SteeringAINodeId { get; set; }
    public string? SteeringAINodeName { get; set; }
    public string? SteeringAIPromptPrefix { get; set; }  // Custom instructions prepended to auto-generated prompt
    
    // Evaluation Settings
    public EvaluationType EvaluationType { get; set; } = EvaluationType.Expression;
    public string? SuccessCriteria { get; set; } // Expression or AI prompt
    public string? EvaluatorAIProvider { get; set; } // e.g., "Anthropic", "OpenAI"
    public string? EvaluatorModel { get; set; }
    
    // Registered Agents (discovered from canvas connections)
    public List<AgentConfig> Agents { get; set; } = new();
    
    // Registered Tool Chains (discovered from canvas connections)
    public List<ToolChainConfig> ToolChains { get; set; } = new();
    
    // Output Tags
    public List<string> OutputTags { get; set; } = new() { "complete", "error" };
    
    // Execution History
    public string? LastExecutedAt { get; set; }
    public int? LastIterationCount { get; set; }
    public bool? LastSuccess { get; set; }
}

public enum EvaluationType
{
    Expression,
    AIEvaluation
}

/// <summary>
/// Configuration for a registered agent.
/// </summary>
public class AgentConfig
{
    public Guid NodeId { get; set; }
    public string RoleName { get; set; } = "";
    public string? NodeType { get; set; } // e.g., "Anthropic", "Mistral"
    public string SkillDescription { get; set; } = ""; // User-provided skill for steering AI
    public string SystemPrompt { get; set; } = "";
    public List<string>? AssignedTools { get; set; } // e.g., ["tool:sql", "tool:http"]
    public int ExecutionOrder { get; set; } = 0;
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Configuration for a registered tool chain.
/// </summary>
public class ToolChainConfig
{
    public string ToolTag { get; set; } = ""; // e.g., "tool:sql"
    public Guid EntryNodeId { get; set; }
    public string? EntryNodeType { get; set; }
    public int BranchCount { get; set; } = 1;
    public int TotalNodeCount { get; set; } = 1;
    public int? TimeoutSeconds { get; set; } = 60;
}

#endregion

#region Runtime Models

/// <summary>
/// Runtime information about a connected agent.
/// </summary>
public class ConnectedAgent
{
    public Guid NodeId { get; set; }
    public string RoleName { get; set; } = "";
    public string NodeType { get; set; } = "";
    public string SkillDescription { get; set; } = "";  // User-provided skill for steering AI
    public string SystemPrompt { get; set; } = "";
    public List<string> AssignedTools { get; set; } = new();
    public int ExecutionOrder { get; set; }
}

/// <summary>
/// Runtime information about a tool chain.
/// </summary>
public class ToolChainInfo
{
    public string ToolTag { get; set; } = "";
    public Guid EntryNodeId { get; set; }
    public string EntryNodeType { get; set; } = "";
    public int BranchCount { get; set; }
    public int TotalNodeCount { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public List<ToolChainBranch> Branches { get; set; } = new();
}

/// <summary>
/// A single branch within a tool chain.
/// </summary>
public class ToolChainBranch
{
    public List<ChainNode> Nodes { get; set; } = new();
    public Guid TerminalNodeId { get; set; }
    public string TerminalNodeType { get; set; } = "";
    public List<string> AvailableOutputs { get; set; } = new();
}

/// <summary>
/// A node within a tool chain branch.
/// </summary>
public class ChainNode
{
    public Guid NodeId { get; set; }
    public string NodeName { get; set; } = "";
    public string NodeType { get; set; } = "";
}

/// <summary>
/// Tracks orchestrator execution state across iterations.
/// </summary>
public class OrchestratorExecutionState
{
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int CurrentIteration { get; set; }
    public int MaxIterations { get; set; }
    public bool IsSuccess { get; set; }
    public string FinalEvaluation { get; set; } = "";
    public int TotalToolCalls { get; set; }
    public List<AgentExecutionRecord> AgentExecutions { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Record of a single agent execution within an iteration.
/// </summary>
public class AgentExecutionRecord
{
    public string RoleName { get; set; } = "";
    public Guid NodeId { get; set; }
    public int Iteration { get; set; }
    public string Response { get; set; } = "";
    public DateTime ExecutedAt { get; set; }
}

#endregion
