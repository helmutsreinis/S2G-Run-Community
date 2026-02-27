using Microsoft.AspNetCore.Components;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;
using S2GPulseWeb.Web.Logic.Nodes;
using System.Text.Json;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: Execution event handlers, workflow start/stop, node execution pipeline.
/// </summary>
public partial class Designer
{
    // Animation event handlers
    private void HandleConnectionTraversalStarted(Guid connectionId)
    {
        InvokeAsync(() =>
        {
            activeConnectionIds.Add(connectionId);
            StateHasChanged();
        });
    }

    private void HandleConnectionTraversalEnded(Guid connectionId)
    {
        InvokeAsync(() =>
        {
            activeConnectionIds.Remove(connectionId);
            StateHasChanged();
        });
    }

    private void HandleNodeExecutionStarted(Guid workflowId, Guid nodeId)
    {
        // Only update if this is the currently viewed workflow
        if (currentWorkflowId != workflowId) return;
        
        InvokeAsync(() =>
        {
            var node = canvasNodes.FirstOrDefault(n => n.Id == nodeId);
            if (node != null)
            {
                node.Status = NodeStatus.Running;
                executingNodeIds.Add(nodeId);
                StateHasChanged();
            }
        });
    }

    private void HandleNodeExecutionCompleted(Guid workflowId, Guid nodeId)
    {
        // Only update if this is the currently viewed workflow
        if (currentWorkflowId != workflowId) return;
        
        InvokeAsync(() =>
        {
            var node = canvasNodes.FirstOrDefault(n => n.Id == nodeId);
            if (node != null)
            {
                node.Status = NodeStatus.Success;
                executingNodeIds.Remove(nodeId);
                StateHasChanged();
            }
        });
    }

    /// <summary>
    /// Handler for when a node's output data is updated during execution.
    /// Syncs the output data to the canvas node so surface fields can display real-time values.
    /// </summary>
    private void HandleNodeOutputDataUpdated(Guid nodeId, Dictionary<string, object?> outputData)
    {
        InvokeAsync(() =>
        {
            var node = canvasNodes.FirstOrDefault(n => n.Id == nodeId);
            if (node != null)
            {
                foreach (var kvp in outputData)
                {
                    node.OutputData[kvp.Key] = kvp.Value;
                }
                StateHasChanged();
            }
        });
    }

    private void HandleNodeLogAdded(Guid nodeId, NodeLogEntry log)
    {
        var node = canvasNodes.FirstOrDefault(n => n.Id == nodeId);
        if (node != null)
        {
            InvokeAsync(() => 
            {
                // Only add if not already present (prevent duplicates from BaseNodeExecutor.Log)
                if (!node.ActivityLogs.Any(l => l.Timestamp == log.Timestamp && l.Message == log.Message))
                {
                    node.ActivityLogs.Add(log);
                }
                
                // If it's a trigger node (like HttpListener), store the data for UI display
                if (node.IsTrigger && !string.IsNullOrEmpty(log.Detail))
                {
                    try
                    {
                        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(log.Detail);
                        if (data != null)
                        {
                            foreach (var kvp in data)
                            {
                                node.OutputData[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch { }
                }

                StateHasChanged();
            });
        }
    }

    private void HandleNodeConfigurationUpdated(Guid nodeId, string newConfiguration)
    {
        // Find the node in canvas and update its configuration
        var node = canvasNodes.FirstOrDefault(n => n.Id == nodeId);
        if (node != null)
        {
            // For AI nodes, only merge cost/token fields to preserve prompt placeholders
            var isAiNode = node.NodeType is "OpenAI" or "DeepSeek" or "DeepSeekAgent" or "Anthropic" or "Gemini" or "Mistral" or "Groq";
            if (isAiNode)
            {
                node.Configuration = MergeAiCostUpdates(node.Configuration, newConfiguration);
            }
            else
            {
                node.Configuration = newConfiguration;
            }
            InvokeAsync(StateHasChanged);
        }
    }
    
    /// <summary>
    /// Merges only cost/token fields from the updated config back into the original config.
    /// This preserves prompt placeholders while updating Cost, InputTokens, and OutputTokens.
    /// </summary>
    private string MergeAiCostUpdates(string? originalConfigJson, string? updatedConfigJson)
    {
        try
        {
            if (string.IsNullOrEmpty(originalConfigJson)) return updatedConfigJson ?? "{}";
            if (string.IsNullOrEmpty(updatedConfigJson)) return originalConfigJson;

            var originalConfig = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalConfigJson);
            var updatedConfig = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updatedConfigJson);
            
            if (originalConfig == null || updatedConfig == null) return originalConfigJson;

            // Only copy cost/token fields from updated to original
            var costFields = new[] { "Cost", "InputTokens", "OutputTokens" };
            foreach (var field in costFields)
            {
                if (updatedConfig.TryGetValue(field, out var value))
                {
                    originalConfig[field] = value;
                }
            }

            return JsonSerializer.Serialize(originalConfig);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Designer] Error merging AI config updates: {ex.Message}");
            return originalConfigJson ?? "{}";
        }
    }

    private async Task StartWorkflow()
    {
        // Guard 1: Must have triggers to start
        var triggerNodes = canvasNodes.Where(n => n.IsTrigger).ToList();
        if (!triggerNodes.Any())
        {
            workflowNotificationMessage = "Cannot start workflow: No trigger nodes marked. Right-click a node and select 'Mark as Trigger'.";
            workflowNotificationType = "warning";
            return;
        }
        
        // Guard 2: New workflow must be saved first
        if (!currentWorkflowId.HasValue)
        {
            workflowNotificationMessage = "Please save the workflow first before starting it.";
            workflowNotificationType = "warning";
            return;
        }
        
        // Guard 3: Unsaved changes must be saved first
        if (hasUnsavedChanges)
        {
            workflowNotificationMessage = "You have unsaved changes. Please save the workflow before starting it.";
            workflowNotificationType = "warning";
            return;
        }
        
        // Clear any previous notification
        workflowNotificationMessage = null;
        
        // Prepare workflow definition for background execution
        var nodeDefinitions = canvasNodes.Select(n => (
            n.Id,
            n.NodeType,
            n.Name,
            n.Configuration,
            n.IsTrigger,
            n.LoggingSettings
        )).ToList();
        
        var connectionDefinitions = connections.Select(c => (c.Id, c.SourceId, c.TargetId, c.Label)).ToList();
        
        // Start workflow in background service (handles execution independently)
        var (success, error) = await WorkflowExecutionService.StartWorkflowAsync(
            currentWorkflowId.Value, 
            currentUserId ?? "", 
            currentWorkflowName,
            nodeDefinitions,
            connectionDefinitions,
            currentWorkflowOrganizationId);
        
        if (!success)
        {
            workflowNotificationMessage = error ?? "Cannot start workflow: Execution limit reached.";
            workflowNotificationType = "error";
        }
        
        StateHasChanged();
    }

    private async Task StopWorkflow()
    {
        // Stop all running node executions
        foreach (var node in canvasNodes)
        {
            if (ExecutionManager.IsRunning(node.Id))
            {
                ExecutionManager.StopNode(node.Id);
            }
        }
        executingNodeIds.Clear();
        
        // Update execution service
        if (currentWorkflowId.HasValue)
        {
            await WorkflowExecutionService.StopWorkflowAsync(currentWorkflowId.Value);
        }
        
        StateHasChanged();
    }

    private async Task ExecuteNode(CanvasNode node)
    {
        try
        {
            // Ensure workflow is saved before execution for DB consistency
            if (!currentWorkflowId.HasValue || currentWorkflowId == Guid.Empty)
            {
                // Auto-save the workflow before first execution
                await SaveWorkflow();
                if (!currentWorkflowId.HasValue)
                {
                    node.ActivityLogs.Add(new NodeLogEntry 
                    { 
                        Message = "Please save the workflow before running nodes", 
                        Level = NodeLogLevel.Warning 
                    });
                    return;
                }
            }
            
            // Track this node as executing for animation
            executingNodeIds.Add(node.Id);
            _ = InvokeAsync(StateHasChanged);
            
            var executor = ExecutorFactory.CreateExecutor(node.NodeType);
            var inputData = new Dictionary<string, object?>(node.OutputData);
            
            // Inject workflow context for agent nodes that need to execute tool nodes
            if (currentWorkflowId.HasValue)
            {
                inputData["_WorkflowId"] = currentWorkflowId.Value;
                inputData["_WorkflowExecutionService"] = WorkflowExecutionService;
            }
            // Inject direct execution context for "Run Node" mode (workflow not in running state)
            inputData["_ExecutorFactory"] = ExecutorFactory;
            inputData["_CanvasNodes"] = canvasNodes;
            inputData["_UserId"] = currentUserId;
            
            // Resolve placeholders in configuration
            var resolvedConfig = PlaceholderHelperInstance.ResolvePlaceholders(node.Configuration, node, canvasNodes);
            
            var workflowNode = new WorkflowNode
            {
                Id = node.Id,
                NodeType = node.NodeType,
                Name = node.Name,
                Configuration = resolvedConfig,
                Status = node.Status,
                ActivityLogs = node.ActivityLogs
            };

            var result = await executor.ExecuteAsync(workflowNode, inputData, currentUserId ?? "");
            node.Status = workflowNode.Status;
            
            // Update node output data from execution result if any
            if (result.OutputData != null)
            {
                foreach (var kvp in result.OutputData)
                {
                    node.OutputData[kvp.Key] = kvp.Value;
                }
                
                // Update node configuration with execution samples for dynamic placeholders
                if (result.Success)
                {
                    PlaceholderHelperInstance.UpdateNodeConfigWithSamples(node, result.OutputData);
                }
            }
            
            // Trigger downstream nodes after successful execution
            if (result.Success)
            {
                await ExecuteDownstream(node);
            }
        }
        catch (Exception ex)
        {
            node.Status = NodeStatus.Failure;
            node.ActivityLogs.Add(new NodeLogEntry { Message = $"Fatal Error: {ex.Message}", Level = NodeLogLevel.Error });
        }
        finally
        {
            // Remove from executing set when done
            executingNodeIds.Remove(node.Id);
            
            // Persist logs to database
            // Skip if workflow is running via WorkflowExecutionService (it handles log persistence)
            try
            {
                var isWorkflowRunningInBackground = currentWorkflowId.HasValue && 
                    WorkflowExecutionService.IsRunning(currentWorkflowId.Value);
                
                if (!isWorkflowRunningInBackground && !string.IsNullOrEmpty(currentUserId) && node.ActivityLogs.Any())
                {
                    // Filter logs based on node's logging settings
                    var logsToSave = node.ActivityLogs
                        .Where(l => ShouldPersistLog(node.LoggingSettings, l.Level))
                        .Select(l => new NodeLog
                        {
                            Id = Guid.NewGuid(), // Generate new ID for database
                            UserId = currentUserId,
                            WorkflowId = currentWorkflowId,
                            NodeId = node.Id,
                            NodeName = node.Name,
                            NodeType = node.NodeType,
                            Timestamp = l.Timestamp,
                            Level = l.Level,
                            Message = l.Message,
                            Detail = l.Detail
                        }).ToList();

                    if (logsToSave.Any())
                    {
                        await LogService.SaveLogsAsync(logsToSave);
                    }
                    
                    // Clear the in-memory logs after persisting to avoid duplicates
                    node.ActivityLogs.Clear();
                }
            }
            catch (Exception logEx)
            {
                // Don't fail execution due to log persistence issues
                Console.WriteLine($"Failed to persist logs: {logEx.Message}");
            }
            
            _ = InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Determines if a log entry should be persisted based on node's logging settings.
    /// </summary>
    private static bool ShouldPersistLog(NodeLoggingSettings settings, NodeLogLevel level)
    {
        if (!settings.LoggingEnabled) return false;
        
        return level switch
        {
            NodeLogLevel.Info => settings.LogInfo,
            NodeLogLevel.Warning => settings.LogWarning,
            NodeLogLevel.Error => settings.LogError,
            NodeLogLevel.Debug => settings.LogDebug,
            _ => false
        };
    }

    private async Task ExecuteDownstream(CanvasNode startNode)
    {
        // Get downstream connections, excluding special connection types (data-only or controlled by source node):
        // - "reader" and "storage": data-only connections, no execution flow
        // - "agent": orchestrator-to-agent connections (controlled by Orchestrator node)
        // - "tool:*": agent-to-tool connections (controlled by Agent node - tools are executed via direct invocation)
        var nextConnections = connections
            .Where(c => c.SourceId == startNode.Id)
            .Where(c => !string.Equals(c.Label, "reader", StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.Equals(c.Label, "agent", StringComparison.OrdinalIgnoreCase))
            .Where(c => !(c.Label?.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) == true))
            .ToList();
        
        // If this is a Condition node, filter connections based on result
        if (startNode.NodeType == "Condition" && startNode.OutputData.TryGetValue("ConditionResult", out var resultObj))
        {
            var result = resultObj is bool b ? b : resultObj?.ToString()?.ToLower() == "true";
            var targetLabel = result ? "true" : "false";
            nextConnections = nextConnections.Where(c => 
                string.Equals(c.Label, targetLabel, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        
        foreach (var conn in nextConnections)
        {
            var nextNode = canvasNodes.FirstOrDefault(n => n.Id == conn.TargetId);
            if (nextNode != null)
            {
                try
                {
                    // Mark this connection as active for animation
                    activeConnectionIds.Add(conn.Id);
                    await InvokeAsync(StateHasChanged);
                    
                    // Pass output data to next node (exclude ConditionResult to prevent stale branching data)
                    foreach (var kvp in startNode.OutputData)
                    {
                        if (kvp.Key != "ConditionResult")
                        {
                            nextNode.OutputData[kvp.Key] = kvp.Value;
                        }
                    }
                    
                    // Clear any stale ConditionResult in the next node before execution
                    nextNode.OutputData.Remove("ConditionResult");

                    await ExecuteNode(nextNode);
                    
                    // Clear this connection's active state
                    activeConnectionIds.Remove(conn.Id);
                    await InvokeAsync(StateHasChanged);
                    
                    // Recurse downstream (ExecuteNode now handles this, skip to avoid double-recursion)
                }
                catch (Exception ex)
                {
                    startNode.ActivityLogs.Add(new NodeLogEntry 
                    { 
                        Level = NodeLogLevel.Error, 
                        Message = $"Failed to execute downstream: {nextNode.Name}",
                        Detail = ex.Message
                    });
                }
            }
        }
    }
}
