using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using S2GPulseWeb.Web.Components.Pages.Workflow.Designer;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Singleton service for managing workflow-level execution state
/// Executes workflows independently of the Designer page
/// </summary>
public class WorkflowExecutionService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NodeExecutionManager _executionManager;
    private readonly CacheStorageService _cacheStorageService;
    private readonly ConcurrentDictionary<Guid, WorkflowRunState> _runningWorkflows = new();
    
    // Track nodes currently being executed under orchestrator control (suppress downstream triggering)
    private readonly ConcurrentDictionary<Guid, bool> _orchestratorControlledNodes = new();

    // Animation events for Designer UI
    public event Action<Guid>? OnConnectionTraversalStarted;  // connectionId
    public event Action<Guid>? OnConnectionTraversalEnded;    // connectionId
    public event Action<Guid, Guid>? OnNodeExecutionStarted;  // workflowId, nodeId
    public event Action<Guid, Guid>? OnNodeExecutionCompleted; // workflowId, nodeId
    
    // Data sync event for Designer surface fields
    public event Action<Guid, Dictionary<string, object?>>? OnNodeOutputDataUpdated; // nodeId, outputData

    public WorkflowExecutionService(IServiceScopeFactory scopeFactory, NodeExecutionManager executionManager, CacheStorageService cacheStorageService)
    {
        _scopeFactory = scopeFactory;
        _executionManager = executionManager;
        _cacheStorageService = cacheStorageService;
        
        // Subscribe to node output events for downstream triggering
        _executionManager.OnNodeOutputDataReceived += HandleNodeOutputDataReceived;
        
        // Subscribe to log events for persistence
        _executionManager.OnNodeLogAdded += HandleNodeLogAdded;
    }

    private void HandleNodeOutputDataReceived(Guid nodeId, Dictionary<string, object?> outputData)
    {
        // Skip downstream triggering for nodes under orchestrator control
        if (_orchestratorControlledNodes.ContainsKey(nodeId))
        {
            Console.WriteLine($"[WorkflowExecutionService] Skipping downstream trigger for orchestrator-controlled node {nodeId}");
            return;
        }

        Console.WriteLine($"[WorkflowExecutionService] OnNodeOutputDataReceived for node {nodeId}. Running workflows: {_runningWorkflows.Count}");
        
        // Find which workflow this node belongs to and trigger downstream
        foreach (var (workflowId, state) in _runningWorkflows)
        {
            Console.WriteLine($"[WorkflowExecutionService] Checking workflow {workflowId} with {state.Nodes.Count} nodes");
            var node = state.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node != null)
            {
                Console.WriteLine($"[WorkflowExecutionService] Found node {node.Name} in workflow {workflowId}. Triggering downstream...");
                
                // If this is a trigger node, start a new execution cycle
                if (node.IsTrigger)
                {
                    state.CurrentExecutionId = Guid.NewGuid();
                    state.CurrentSourceNodeId = nodeId; // Track which trigger started this execution
                    state.CurrentExecutionCompletedNodes.Clear();
                    
                    // Track the source trigger node ID so downstream nodes know where the request originated
                    // This is critical for workflows with multiple listeners where nodes need to filter by source
                    outputData["_SourceNodeId"] = nodeId;
                    
                    Console.WriteLine($"[WorkflowExecutionService] Started new execution cycle: {state.CurrentExecutionId}, SourceNodeId: {nodeId}");
                }
                
                // Store output data in node
                foreach (var kvp in outputData)
                {
                    node.OutputData[kvp.Key] = kvp.Value;
                }
                
                // Notify Designer to update surface fields
                OnNodeOutputDataUpdated?.Invoke(nodeId, outputData);
                
                // Mark this node as completed in current execution
                state.CurrentExecutionCompletedNodes.Add(nodeId);
                
                // Update execution count
                state.ExecutionCount++;
                state.LastExecutedAt = DateTime.UtcNow;
                
                // Track billable execution for usage limits (org or personal based on workflow context)
                var currentUserId = state.UserId; // Capture for async closure
                var currentOrgId = state.OrganizationId; // Capture for async closure
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var usageScope = _scopeFactory.CreateScope();
                        
                        if (currentOrgId.HasValue)
                        {
                            // Org workflow - track against organization limits
                            var orgUsageService = usageScope.ServiceProvider.GetRequiredService<OrganizationUsageTrackingService>();
                            await orgUsageService.IncrementExecutionCountAsync(currentOrgId.Value);
                            // Note: Org execution limit enforcement is handled separately
                        }
                        else
                        {
                            // Personal workflow - track against user limits
                            var usageTrackingService = usageScope.ServiceProvider.GetRequiredService<UsageTrackingService>();
                            var result = await usageTrackingService.IncrementExecutionCountAsync(currentUserId);
                            
                            // When limit is reached, stop ALL running workflows for this user
                            if (result.LimitReached)
                            {
                                Console.WriteLine($"[WorkflowExecutionService] User {currentUserId} reached execution limit ({result.CurrentCount:N0}/{result.Limit:N0}). Stopping all workflows.");
                                
                                // Get WorkflowExecutionService from scope to stop all workflows
                                var workflowService = usageScope.ServiceProvider.GetRequiredService<WorkflowExecutionService>();
                                await workflowService.StopAllUserWorkflowsAsync(
                                    currentUserId,
                                    $"Monthly execution limit reached ({result.CurrentCount:N0}/{result.Limit:N0})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WorkflowExecutionService] Error tracking execution: {ex.Message}");
                    }
                });
                
                // Persist sample data to node configuration for dynamic placeholder detection
                _ = Task.Run(async () =>
                {
                    await UpdateNodeConfigWithSamplesAsync(workflowId, nodeId, node, outputData);
                });
                
                // Trigger downstream nodes asynchronously (unless suppressed by Orchestrator control)
                var shouldSuppress = outputData.TryGetValue("_SuppressDownstreamTrigger", out var suppress) && suppress is true;
                if (!shouldSuppress)
                {
                    var capturedOutputData = new Dictionary<string, object?>(outputData); // Capture for async closure
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Console.WriteLine($"[WorkflowExecutionService] Calling TriggerDownstreamNodesAsync for workflow {workflowId}, node {nodeId}");
                            await TriggerDownstreamNodesAsync(workflowId, nodeId, capturedOutputData);
                            Console.WriteLine($"[WorkflowExecutionService] TriggerDownstreamNodesAsync completed");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error triggering downstream nodes: {ex.Message}");
                        }
                    });
                }
                else
                {
                    Console.WriteLine($"[WorkflowExecutionService] Downstream triggering SUPPRESSED for node {nodeId} (orchestrator-controlled)");
                }
                break;
            }
        }
    }

    /// <summary>
    /// Updates node configuration with execution samples for dynamic placeholder detection
    /// </summary>
    private async Task UpdateNodeConfigWithSamplesAsync(Guid workflowId, Guid nodeId, WorkflowNodeState node, Dictionary<string, object?> outputData)
    {
        try
        {
            // Parse current configuration
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

            // HTTP Request: _ResponseSample -> LastResponseSample
            if (outputData.TryGetValue("_ResponseSample", out var responseSample) && responseSample != null)
            {
                var responseSampleStr = responseSample.ToString() ?? "";
                if (responseSampleStr.Length > 10240)
                {
                    responseSampleStr = responseSampleStr.Substring(0, 10240);
                }
                config["LastResponseSample"] = responseSampleStr;
                updated = true;
            }

            // Loop Node: _LoopSample -> LastInputArraySample
            if (outputData.TryGetValue("_LoopSample", out var loopSample) && loopSample != null)
            {
                var loopSampleStr = loopSample.ToString() ?? "";
                if (loopSampleStr.Length > 5120)
                {
                    loopSampleStr = loopSampleStr.Substring(0, 5120);
                }
                config["LastInputArraySample"] = loopSampleStr;
                updated = true;
            }

            // Azure Queue Monitor: _MessageSample -> LastMessageSample
            if (outputData.TryGetValue("_MessageSample", out var messageSample) && messageSample != null)
            {
                var messageSampleStr = messageSample.ToString() ?? "";
                if (messageSampleStr.Length > 10240)
                {
                    messageSampleStr = messageSampleStr.Substring(0, 10240);
                }
                config["LastMessageSample"] = messageSampleStr;
                updated = true;
            }

            if (updated)
            {
                var newConfigJson = JsonSerializer.Serialize(config);
                
                // Update in-memory state
                node.Configuration = newConfigJson;
                
                // Persist to database
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                
                var dbNode = await dbContext.WorkflowNodes.FindAsync(nodeId);
                if (dbNode != null)
                {
                    dbNode.Configuration = newConfigJson;
                    await dbContext.SaveChangesAsync();
                    Console.WriteLine($"[WorkflowExecutionService] Updated node {node.Name} config with sample data");
                    
                    // Notify Designer to refresh the node configuration in real-time
                    _executionManager.NotifyConfigurationUpdated(nodeId, newConfigJson);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WorkflowExecutionService] Error updating node config with samples: {ex.Message}");
        }
    }

    private void HandleNodeLogAdded(Guid nodeId, NodeLogEntry logEntry)
    {
        // Find which workflow this node belongs to
        foreach (var (workflowId, state) in _runningWorkflows)
        {
            var node = state.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node != null)
            {
                // Check if logging is enabled for this level
                if (!ShouldPersistLog(node.LoggingSettings, logEntry.Level))
                {
                    return; // Skip persistence based on node settings
                }
                
                // Persist log to database asynchronously using a scoped context
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        
                        var nodeLog = new NodeLog
                        {
                            Id = Guid.NewGuid(),
                            UserId = state.UserId,
                            WorkflowId = workflowId,
                            NodeId = nodeId,
                            NodeName = node.Name,
                            NodeType = node.NodeType,
                            Level = logEntry.Level,
                            Message = logEntry.Message,
                            Detail = logEntry.Detail,
                            Timestamp = logEntry.Timestamp
                        };
                        
                        dbContext.NodeLogs.Add(nodeLog);
                        await dbContext.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error persisting log: {ex.Message}");
                    }
                });
                break;
            }
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

    /// <summary>
    /// In-memory state for a running workflow
    /// </summary>
    private class WorkflowRunState
    {
        public Guid WorkflowId { get; set; }
        public string UserId { get; set; } = "";
        public string WorkflowName { get; set; } = "";
        public Guid? OrganizationId { get; set; } // For org-scoped workflows
        public DateTime StartedAt { get; set; }
        public int ExecutionCount { get; set; }
        public DateTime? LastExecutedAt { get; set; }
        
        // Tracking for current execution cycle (reset each time trigger fires)
        public Guid CurrentExecutionId { get; set; } = Guid.Empty;
        public Guid CurrentSourceNodeId { get; set; } = Guid.Empty; // The trigger node ID for current execution
        public HashSet<Guid> CurrentExecutionCompletedNodes { get; set; } = new();
        
        // Workflow definition for background execution
        public List<WorkflowNodeState> Nodes { get; set; } = new();
        public List<WorkflowConnectionState> Connections { get; set; } = new();
    }

    private class WorkflowNodeState
    {
        public Guid Id { get; set; }
        public string NodeType { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Configuration { get; set; }
        public bool IsTrigger { get; set; }
        public NodeLoggingSettings LoggingSettings { get; set; } = new();
        public Dictionary<string, object?> OutputData { get; set; } = new();
    }

    private class WorkflowConnectionState
    {
        public Guid Id { get; set; }
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public string? Label { get; set; } // For condition branching (true/false)
    }

    /// <summary>
    /// Start a workflow with full definition for background execution.
    /// Returns false if user/org has reached execution or storage limit.
    /// </summary>
    public async Task<(bool Success, string? Error)> StartWorkflowAsync(Guid workflowId, string userId, string workflowName,
        List<(Guid Id, string NodeType, string Name, string? Configuration, bool IsTrigger, NodeLoggingSettings LoggingSettings)> nodes,
        List<(Guid Id, Guid SourceId, Guid TargetId, string? Label)> connections,
        Guid? organizationId = null)
    {
        // Check if user/org can execute before starting
        using var checkScope = _scopeFactory.CreateScope();
        
        if (organizationId.HasValue)
        {
            // Org workflow - check org limits
            var orgUsageService = checkScope.ServiceProvider.GetRequiredService<OrganizationUsageTrackingService>();
            
            var (canExecute, execReason) = await orgUsageService.CanExecuteAsync(organizationId.Value);
            if (!canExecute)
            {
                Console.WriteLine($"[WorkflowExecutionService] Cannot start workflow for org {organizationId}: {execReason}");
                return (false, execReason);
            }
            
            var (canStore, storeReason) = await orgUsageService.CanStoreAsync(organizationId.Value);
            if (!canStore)
            {
                Console.WriteLine($"[WorkflowExecutionService] Cannot start workflow for org {organizationId}: {storeReason}");
                return (false, storeReason);
            }
        }
        else
        {
            // Personal workflow - check user limits
            var usageTrackingService = checkScope.ServiceProvider.GetRequiredService<UsageTrackingService>();
            
            var (canExecute, execReason) = await usageTrackingService.CanExecuteAsync(userId);
            if (!canExecute)
            {
                Console.WriteLine($"[WorkflowExecutionService] Cannot start workflow for user {userId}: {execReason}");
                return (false, execReason);
            }
            
            var (canStore, storeReason) = await usageTrackingService.CanStoreAsync(userId);
            if (!canStore)
            {
                Console.WriteLine($"[WorkflowExecutionService] Cannot start workflow for user {userId}: {storeReason}");
                return (false, storeReason);
            }
        }
        
        var state = new WorkflowRunState
        {
            WorkflowId = workflowId,
            UserId = userId,
            WorkflowName = workflowName,
            OrganizationId = organizationId,
            StartedAt = DateTime.UtcNow,
            ExecutionCount = 0,
            Nodes = nodes.Select(n => new WorkflowNodeState
            {
                Id = n.Id,
                NodeType = n.NodeType,
                Name = n.Name,
                Configuration = n.Configuration,
                IsTrigger = n.IsTrigger,
                LoggingSettings = n.LoggingSettings
            }).ToList(),
            Connections = connections.Select(c => new WorkflowConnectionState
            {
                Id = c.Id,
                SourceId = c.SourceId,
                TargetId = c.TargetId,
                Label = c.Label
            }).ToList()
        };
        
        _runningWorkflows[workflowId] = state;

        // Persist to database
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var execution = await context.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.WorkflowId == workflowId);

        if (execution == null)
        {
            execution = new WorkflowExecution
            {
                WorkflowId = workflowId,
                UserId = userId,
                Status = WorkflowExecutionStatus.Running,
                StartedAt = DateTime.UtcNow,
                ExecutionCount = 0
            };
            context.WorkflowExecutions.Add(execution);
        }
        else
        {
            execution.Status = WorkflowExecutionStatus.Running;
            execution.StartedAt = DateTime.UtcNow;
            execution.StoppedAt = null;
        }

        await context.SaveChangesAsync();

        // Execute all trigger nodes in background
        _ = Task.Run(async () =>
        {
            try
            {
                var triggerNodes = state.Nodes.Where(n => n.IsTrigger).ToList();
                foreach (var triggerNode in triggerNodes)
                {
                    await ExecuteNodeInternalAsync(workflowId, triggerNode.Id);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Background workflow execution error: {ex.Message}");
            }
        });
        
        return (true, null);
    }

    /// <summary>
    /// Execute a specific node by ID within a running workflow.
    /// Used by Orchestrator to trigger agent nodes directly.
    /// </summary>
    public async Task ExecuteNodeByIdAsync(Guid workflowId, Guid nodeId, Dictionary<string, object?>? inputData = null)
    {
        if (!_runningWorkflows.TryGetValue(workflowId, out var state)) 
        {
            Console.WriteLine($"[WorkflowExecutionService] ExecuteNodeByIdAsync: Workflow {workflowId} not found");
            return;
        }
        
        var node = state.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) 
        {
            Console.WriteLine($"[WorkflowExecutionService] ExecuteNodeByIdAsync: Node {nodeId} not found in workflow");
            return;
        }

        Console.WriteLine($"[WorkflowExecutionService] ExecuteNodeByIdAsync: Executing {node.Name} ({node.NodeType})");
        
        // If input data is provided, merge it into node's output so it's available as upstream data
        if (inputData != null)
        {
            foreach (var kvp in inputData)
            {
                node.OutputData[kvp.Key] = kvp.Value;
            }
        }
        
        // Execute the node
        await ExecuteNodeInternalAsync(workflowId, nodeId);
    }

    /// <summary>
    /// Execute a node synchronously and return its output data.
    /// Awaits full completion of the node (including any internal tool-calling loops).
    /// Used by Orchestrator to capture agent responses after task completion.
    /// NOTE: This method SUPPRESSES downstream triggering to prevent cascade loops.
    /// </summary>
    public async Task<Dictionary<string, object?>> ExecuteNodeAndGetOutputAsync(
        Guid workflowId, 
        Guid nodeId, 
        Dictionary<string, object?>? inputData = null)
    {
        if (!_runningWorkflows.TryGetValue(workflowId, out var state))
        {
            Console.WriteLine($"[WorkflowExecutionService] ExecuteNodeAndGetOutputAsync: Workflow {workflowId} not found");
            return new Dictionary<string, object?> { { "Error", "Workflow not found" } };
        }
        
        var node = state.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
        {
            Console.WriteLine($"[WorkflowExecutionService] ExecuteNodeAndGetOutputAsync: Node {nodeId} not found");
            return new Dictionary<string, object?> { { "Error", "Node not found" } };
        }
        
        Console.WriteLine($"[WorkflowExecutionService] ExecuteNodeAndGetOutputAsync: Executing {node.Name} ({node.NodeType})");
        
        // Merge input data into node's output so it's available as upstream data
        if (inputData != null)
        {
            foreach (var kvp in inputData)
            {
                node.OutputData[kvp.Key] = kvp.Value;
            }
        }
        
        // Mark this node as orchestrator-controlled (suppress downstream triggering via event handler)
        _orchestratorControlledNodes[nodeId] = true;
        node.OutputData["_SuppressDownstreamTrigger"] = true;
        
        try
        {
            // SYNCHRONOUSLY await full execution (including agent's tool-calling loop)
            await ExecuteNodeInternalAsync(workflowId, nodeId);
        }
        finally
        {
            // Always clear orchestrator control tracking
            _orchestratorControlledNodes.TryRemove(nodeId, out _);
            node.OutputData.Remove("_SuppressDownstreamTrigger");
        }
        
        // Return output AFTER completion - AIResponse is now accurate
        Console.WriteLine($"[WorkflowExecutionService] ExecuteNodeAndGetOutputAsync: Completed {node.Name}, returning {node.OutputData.Count} output keys");
        return new Dictionary<string, object?>(node.OutputData);
    }

    /// <summary>
    /// Execute a node with AI-provided parameter overrides.
    /// Used by AI agents for tool calling - injects parameters into config placeholders.
    /// </summary>
    public async Task<Dictionary<string, object?>> ExecuteToolWithParametersAsync(
        Guid workflowId,
        Guid nodeId,
        Dictionary<string, object?> parameterOverrides,
        Dictionary<string, object?> inputData)
    {
        if (!_runningWorkflows.TryGetValue(workflowId, out var state))
        {
            Console.WriteLine($"[WorkflowExecutionService] ExecuteToolWithParametersAsync: Workflow {workflowId} not found");
            return new Dictionary<string, object?> { { "Error", "Workflow not found" } };
        }

        var node = state.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
        {
            Console.WriteLine($"[WorkflowExecutionService] ExecuteToolWithParametersAsync: Node {nodeId} not found");
            return new Dictionary<string, object?> { { "Error", "Node not found" } };
        }

        Console.WriteLine($"[WorkflowExecutionService] ExecuteToolWithParametersAsync: Executing {node.Name} ({node.NodeType}) with {parameterOverrides.Count} overrides");

        // Store original config for restoration
        var originalConfig = node.Configuration;

        try
        {
            // Inject AI-provided parameters into node configuration JSON properties
            // The agent sends mapped property names (e.g., "Operation", "FilePath", "Content")
            if (!string.IsNullOrEmpty(node.Configuration) && parameterOverrides.Count > 0)
            {
                try
                {
                    var configDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(node.Configuration)
                        ?? new Dictionary<string, System.Text.Json.JsonElement>();
                    
                    // Create modifiable config
                    var modifiableConfig = new Dictionary<string, object?>();
                    foreach (var kvp in configDict)
                    {
                        modifiableConfig[kvp.Key] = kvp.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => kvp.Value.GetString(),
                            System.Text.Json.JsonValueKind.Number => kvp.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            System.Text.Json.JsonValueKind.Null => null,
                            _ => kvp.Value.GetRawText()
                        };
                    }
                    
                    // Inject AI-provided parameters, logging what we inject
                    foreach (var param in parameterOverrides)
                    {
                        Console.WriteLine($"[WorkflowExecutionService] Injecting param: {param.Key} = {param.Value?.ToString()?.Substring(0, Math.Min(50, param.Value?.ToString()?.Length ?? 0))}...");
                        modifiableConfig[param.Key] = param.Value;
                    }
                    
                    node.Configuration = System.Text.Json.JsonSerializer.Serialize(modifiableConfig);
                }
                catch
                {
                    // Fallback to placeholder replacement if JSON parsing fails
                    var modifiedConfig = node.Configuration;
                    foreach (var param in parameterOverrides)
                    {
                        var placeholder = $"{{{{{param.Key}}}}}";
                        modifiedConfig = modifiedConfig.Replace(placeholder, param.Value?.ToString() ?? "");
                    }
                    node.Configuration = modifiedConfig;
                }
            }

            // Merge input data with parameter overrides
            var mergedInputData = new Dictionary<string, object?>(inputData);
            foreach (var param in parameterOverrides)
            {
                mergedInputData[param.Key] = param.Value;
            }

            // Also update node's output data with the parameters so they're available as upstream data
            foreach (var param in parameterOverrides)
            {
                node.OutputData[param.Key] = param.Value;
            }

            // Execute the node
            await ExecuteNodeInternalAsync(workflowId, nodeId);

            // Return the node's output data
            return new Dictionary<string, object?>(node.OutputData);
        }
        finally
        {
            // Restore original configuration to preserve placeholders
            node.Configuration = originalConfig;
        }
    }

    /// <summary>
    /// Execute a node and trigger downstream nodes
    /// </summary>
    private async Task ExecuteNodeInternalAsync(Guid workflowId, Guid nodeId)
    {
        if (!_runningWorkflows.TryGetValue(workflowId, out var state)) return;
        
        var node = state.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return;

        // Check for reader connections - this node may have reader connections TO other nodes
        // that need to complete before this node can read their data
        var readerDependencies = state.Connections
            .Where(c => c.SourceId == nodeId && string.Equals(c.Label, "reader", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.TargetId)
            .ToList();

        if (readerDependencies.Any())
        {
            // Wait briefly for reader dependencies - but don't block too long
            // Best practice: Cache nodes should directly trigger downstream nodes for reliable data flow
            var timeoutMs = 500; // 500ms timeout - just enough for nodes in same execution cycle
            var startTime = DateTime.UtcNow;
            
            foreach (var depNodeId in readerDependencies)
            {
                var waited = false;
                while (!state.CurrentExecutionCompletedNodes.Contains(depNodeId))
                {
                    if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
                    {
                        if (!waited)
                        {
                            Console.WriteLine($"[WorkflowExecutionService] Reader dependency {depNodeId} not completed in time - proceeding anyway");
                        }
                        break;
                    }
                    waited = true;
                    await Task.Delay(10); // Small delay to prevent busy waiting
                }
                if (waited && state.CurrentExecutionCompletedNodes.Contains(depNodeId))
                {
                    Console.WriteLine($"[WorkflowExecutionService] Reader dependency {depNodeId} completed after waiting");
                }
            }
        }

        // Fire animation event - node execution started
        OnNodeExecutionStarted?.Invoke(workflowId, nodeId);

        using var scope = _scopeFactory.CreateScope();
        var executorFactory = scope.ServiceProvider.GetRequiredService<NodeExecutorFactory>();
        
        try
        {
            var executor = executorFactory.CreateExecutor(node.NodeType);
            
            // For StorageClient nodes, resolve the connected StorageTable from "storage" connections
            // Checks both directions for backwards compatibility (Client→Table preferred, Table→Client fallback)
            if (node.NodeType == "StorageClient" && executor is Nodes.StorageClientNode storageClientNode)
            {
                // First check: Client → Table (correct direction)
                var storageConnection = state.Connections
                    .FirstOrDefault(c => c.SourceId == nodeId && 
                                       string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
                if (storageConnection != null)
                {
                    var targetNode = state.Nodes.FirstOrDefault(n => n.Id == storageConnection.TargetId);
                    if (targetNode?.NodeType == "StorageTable")
                    {
                        storageClientNode.SetConnectedStorageTable(storageConnection.TargetId);
                    }
                }
                else
                {
                    // Fallback: Table → Client (reversed direction)
                    storageConnection = state.Connections
                        .FirstOrDefault(c => c.TargetId == nodeId && 
                                           string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
                    if (storageConnection != null)
                    {
                        var sourceNode = state.Nodes.FirstOrDefault(n => n.Id == storageConnection.SourceId);
                        if (sourceNode?.NodeType == "StorageTable")
                        {
                            storageClientNode.SetConnectedStorageTable(storageConnection.SourceId);
                        }
                    }
                }
            }
            
            // For VectorClient nodes, resolve the connected VectorDb from "storage" connections
            // Checks both directions for backwards compatibility (Client→Store preferred, Store→Client fallback)
            if (node.NodeType == "VectorClient" && executor is Nodes.VectorClientNode vectorClientNode)
            {
                // First check: Client → Store (correct direction)
                var storageConnection = state.Connections
                    .FirstOrDefault(c => c.SourceId == nodeId && 
                                       string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
                if (storageConnection != null)
                {
                    var targetNode = state.Nodes.FirstOrDefault(n => n.Id == storageConnection.TargetId);
                    if (targetNode?.NodeType == "VectorDb")
                    {
                        vectorClientNode.SetConnectedVectorStore(storageConnection.TargetId);
                    }
                }
                else
                {
                    // Fallback: Store → Client (reversed direction)
                    storageConnection = state.Connections
                        .FirstOrDefault(c => c.TargetId == nodeId && 
                                           string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
                    if (storageConnection != null)
                    {
                        var sourceNode = state.Nodes.FirstOrDefault(n => n.Id == storageConnection.SourceId);
                        if (sourceNode?.NodeType == "VectorDb")
                        {
                            vectorClientNode.SetConnectedVectorStore(storageConnection.SourceId);
                        }
                    }
                }
            }
            
            // Resolve placeholders in configuration
            var resolvedConfig = ResolveConfigPlaceholders(node.Configuration, node.Id, state);
            
            // Create WorkflowNode object for executor
            var workflowNode = new WorkflowNode
            {
                Id = node.Id,
                NodeType = node.NodeType,
                Name = node.Name,
                Configuration = resolvedConfig
            };
            
            // Collect input data from ALL upstream nodes in the chain (not just immediate predecessors)
            var inputData = new Dictionary<string, object?>();
            var visitedNodes = new HashSet<Guid>();
            CollectUpstreamData(nodeId, state, inputData, visitedNodes);
            
            // Get _SourceNodeId from immediate upstream trigger node (if in the visited chain)
            // Since _SourceNodeId is excluded from CollectUpstreamData, we need to get it from the trigger
            foreach (var triggerId in visitedNodes)
            {
                var triggerNode = state.Nodes.FirstOrDefault(n => n.Id == triggerId && n.IsTrigger);
                if (triggerNode != null && 
                    triggerNode.OutputData.TryGetValue("_SourceNodeId", out var sourceId) &&
                    sourceId is Guid srcGuid && srcGuid == triggerId)
                {
                    // Found a trigger in our upstream chain - use its _SourceNodeId
                    inputData["_SourceNodeId"] = srcGuid;
                    break;
                }
            }
            
            // Inject orchestrator context (if this node was called by Orchestrator with injected prompts)
            // These keys are set by ExecuteNodeAndGetOutputAsync
            foreach (var key in node.OutputData.Keys.Where(k => k.StartsWith("_Orchestrator")))
            {
                inputData[key] = node.OutputData[key];
            }
            
            // Inject workflow context for nodes (like Orchestrator) that need to execute other nodes
            inputData["_WorkflowId"] = workflowId;
            inputData["_WorkflowExecutionService"] = this; // Allow nodes to call ExecuteNodeByIdAsync
            
            // Inject organization context for nodes that need org-scoped storage/resources
            if (state.OrganizationId.HasValue)
            {
                inputData["_OrganizationId"] = state.OrganizationId.Value;
            }
            
            var result = await executor.ExecuteAsync(workflowNode, inputData, state.UserId);
            
            // Preserve orchestrator control flags (if set by ExecuteNodeAndGetOutputAsync)
            if (node.OutputData.TryGetValue("_SuppressDownstreamTrigger", out var suppressFlag) && suppressFlag is true)
            {
                result.OutputData ??= new Dictionary<string, object?>();
                result.OutputData["_SuppressDownstreamTrigger"] = true;
            }
            
            // Store output data
            if (result.OutputData != null)
            {
                foreach (var kvp in result.OutputData)
                {
                    node.OutputData[kvp.Key] = kvp.Value;
                }
                
                // Notify Designer to update surface fields
                OnNodeOutputDataUpdated?.Invoke(nodeId, result.OutputData);
            }
            
            // Persist AI node configuration updates (costs, tokens) to database
            // IMPORTANT: Only merge cost/token fields, preserve original prompts with placeholders
            var isAiNode = node.NodeType is "OpenAI" or "DeepSeek" or "DeepSeekAgent" or "Anthropic" or "Gemini" or "Mistral" or "Groq";
            if (isAiNode && workflowNode.Configuration != resolvedConfig)
            {
                // Merge cost updates from workflowNode into the original node.Configuration (preserving prompts)
                var mergedConfig = MergeAiCostUpdates(node.Configuration, workflowNode.Configuration, node.NodeType);
                node.Configuration = mergedConfig;
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var dbScope = _scopeFactory.CreateScope();
                        var dbContext = dbScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var dbNode = await dbContext.WorkflowNodes.FindAsync(nodeId);
                        if (dbNode != null)
                        {
                            dbNode.Configuration = mergedConfig;
                            await dbContext.SaveChangesAsync();
                            Console.WriteLine($"[WorkflowExecutionService] Persisted AI costs for node {node.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WorkflowExecutionService] Error persisting AI costs: {ex.Message}");
                    }
                });
            }

            // Increment execution count
            state.ExecutionCount++;
            state.LastExecutedAt = DateTime.UtcNow;
            
            // Track billable execution for usage limits (org or personal based on workflow context)
            var currentUserId = state.UserId; // Capture for async closure
            var currentOrgId = state.OrganizationId; // Capture for async closure
            _ = Task.Run(async () =>
            {
                try
                {
                    using var usageScope = _scopeFactory.CreateScope();
                    
                    if (currentOrgId.HasValue)
                    {
                        // Org workflow - track against organization limits
                        var orgUsageService = usageScope.ServiceProvider.GetRequiredService<OrganizationUsageTrackingService>();
                        await orgUsageService.IncrementExecutionCountAsync(currentOrgId.Value);
                        // Note: Org execution limit enforcement is handled separately
                    }
                    else
                    {
                        // Personal workflow - track against user limits
                        var usageTrackingService = usageScope.ServiceProvider.GetRequiredService<UsageTrackingService>();
                        var result = await usageTrackingService.IncrementExecutionCountAsync(currentUserId);
                        
                        // When limit is reached, stop ALL running workflows for this user
                        if (result.LimitReached)
                        {
                            Console.WriteLine($"[WorkflowExecutionService] User {currentUserId} reached execution limit ({result.CurrentCount:N0}/{result.Limit:N0}). Stopping all workflows.");
                            
                            // Get WorkflowExecutionService from scope to stop all workflows
                            var workflowService = usageScope.ServiceProvider.GetRequiredService<WorkflowExecutionService>();
                            await workflowService.StopAllUserWorkflowsAsync(
                                currentUserId,
                                $"Monthly execution limit reached ({result.CurrentCount:N0}/{result.Limit:N0})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WorkflowExecutionService] Error tracking execution: {ex.Message}");
                }
            });

            // Trigger downstream nodes - but skip for trigger nodes and Loop nodes
            // Trigger nodes (like HTTP Listener) trigger downstream via OnNodeOutputDataReceived event
            // Loop nodes trigger downstream for EACH ITERATION via TriggerNodeExecution - don't double-trigger at end
            // EXCEPTION: Custom nodes with _TriggeredTags should always call TriggerDownstreamNodesAsync for tag routing
            
            var hasTriggeredTags = result.OutputData?.ContainsKey("_TriggeredTags") == true;
            var isCustomWithTags = node.NodeType.StartsWith("Custom_") && hasTriggeredTags;
            var shouldTriggerDownstream = (!node.IsTrigger && node.NodeType != "Loop") || isCustomWithTags;
            
            if (shouldTriggerDownstream)
            {
                await TriggerDownstreamNodesAsync(workflowId, nodeId, result.OutputData);
            }
            
            // Mark this node as completed in current execution cycle
            state.CurrentExecutionCompletedNodes.Add(nodeId);
            
            // Fire animation event - node execution completed
            OnNodeExecutionCompleted?.Invoke(workflowId, nodeId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Node execution error: {ex.Message}");
            // Mark as completed even on error so dependents don't hang
            state.CurrentExecutionCompletedNodes.Add(nodeId);
            // Still fire completion event even on error
            OnNodeExecutionCompleted?.Invoke(workflowId, nodeId);
        }
    }

    /// <summary>
    /// Trigger all downstream nodes connected to this node
    /// </summary>
    private async Task TriggerDownstreamNodesAsync(Guid workflowId, Guid nodeId, Dictionary<string, object?>? executionOutputData = null)
    {

        
        // DEBUG: Entry point log
        Console.WriteLine($"[TriggerDownstream-ENTRY] workflowId={workflowId}, nodeId={nodeId}");
        
        if (!_runningWorkflows.TryGetValue(workflowId, out var state)) 
        {
            Console.WriteLine($"[WorkflowExecutionService] TriggerDownstream: Workflow {workflowId} not found");
            return;
        }
        
        Console.WriteLine($"[TriggerDownstream-FOUND] Workflow found, nodes count: {state.Nodes.Count}");

        var sourceNode = state.Nodes.FirstOrDefault(n => n.Id == nodeId);
        
        // Use execution-specific outputData if provided, otherwise fall back to shared node state
        var outputData = executionOutputData ?? sourceNode?.OutputData ?? new Dictionary<string, object?>();
        
        // DEBUG: Log at start to verify this method is being called
        _executionManager.AddNodeLog(nodeId, NodeLogLevel.Info, 
            $"TriggerDownstream: NodeType='{sourceNode?.NodeType}', OutputKeys=[{string.Join(", ", outputData.Keys)}]",
            $"Has _TriggeredTags: {outputData.ContainsKey("_TriggeredTags")}\nNodeType starts with Custom_: {sourceNode?.NodeType?.StartsWith("Custom_") == true}");
        
        // Get downstream connections, excluding special connection types:
        // - "reader" and "storage": data-only, no execution
        // - "agent": orchestrator-to-agent connections (controlled by Orchestrator node)
        // - "orchestrate": steering-AI-to-orchestrator connections (controlled by Orchestrator node)
        // - "tool:*": orchestrator-to-tool connections (controlled by Orchestrator node)
        // - "run:*": RemoteCommand-to-Remote connections (RemoteCommandNode uses static queue methods)
        
        // DEBUG: Log all connections from this node
        var allFromSource = state.Connections.Where(c => c.SourceId == nodeId).ToList();
        Console.WriteLine($"[TriggerDownstream] Source node={sourceNode?.Name}, all connections from source: [{string.Join(", ", allFromSource.Select(c => $"'{c.Label ?? "(none)"}'->{c.TargetId}"))}]");
        
        var downstreamConnections = state.Connections
            .Where(c => c.SourceId == nodeId)
            .Where(c => !string.Equals(c.Label, "reader", StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.Equals(c.Label, "agent", StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.Equals(c.Label, "orchestrate", StringComparison.OrdinalIgnoreCase))
            .Where(c => !(c.Label?.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) == true))
            .Where(c => !(c.Label?.StartsWith("run:", StringComparison.OrdinalIgnoreCase) == true))
            .ToList();
        
        Console.WriteLine($"[TriggerDownstream] After filtering: {downstreamConnections.Count} connections remain");
        
        // Apply Condition node filtering
        if (sourceNode?.NodeType == "Condition" && outputData.TryGetValue("ConditionResult", out var resultObj))
        {
            var result = resultObj is bool b ? b : resultObj?.ToString()?.ToLower() == "true";
            var targetLabel = result ? "true" : "false";
            Console.WriteLine($"[WorkflowExecutionService] Condition node result: {result}, filtering to '{targetLabel}' connections");
            downstreamConnections = downstreamConnections.Where(c => 
                string.Equals(c.Label, targetLabel, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        
        // Apply Orchestrator node filtering - routes based on _TriggeredTags (complete/error)
        if (sourceNode?.NodeType == "Orchestrator" && outputData.TryGetValue("_TriggeredTags", out var orchTagsObj))
        {
            List<string>? triggeredTags = null;
            
            if (orchTagsObj is string tagsJson && !string.IsNullOrEmpty(tagsJson))
            {
                try { triggeredTags = JsonSerializer.Deserialize<List<string>>(tagsJson); }
                catch { Console.WriteLine($"[WorkflowExecutionService] Failed to parse Orchestrator _TriggeredTags: {tagsJson}"); }
            }
            else if (orchTagsObj is List<string> tagsList)
            {
                triggeredTags = tagsList;
            }
            
            if (triggeredTags != null && triggeredTags.Any())
            {
                Console.WriteLine($"[WorkflowExecutionService] Orchestrator routing to tags: [{string.Join(", ", triggeredTags)}]");
                downstreamConnections = downstreamConnections.Where(c => 
                    string.IsNullOrEmpty(c.Label) || 
                    triggeredTags.Any(t => string.Equals(t, c.Label, StringComparison.OrdinalIgnoreCase))).ToList();
            }
        }
        
        // Apply Aggregator node filtering - routes invalid items to "invalid" connections
        if (sourceNode?.NodeType == "Aggregator")
        {
            var isValidationFailed = outputData.TryGetValue("_ValidationFailed", out var failedObj) && 
                                     failedObj is bool failed && failed;
            var isThresholdReached = outputData.TryGetValue("IsThresholdReached", out var thresholdObj) && 
                                     thresholdObj is bool reached && reached;
            
            if (isValidationFailed)
            {
                // Route to "invalid" connections only
                Console.WriteLine($"[WorkflowExecutionService] Aggregator validation failed, filtering to 'invalid' connections");
                downstreamConnections = downstreamConnections.Where(c => 
                    string.Equals(c.Label, "invalid", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (isThresholdReached)
            {
                // Route to "valid" connections (or no label)
                Console.WriteLine($"[WorkflowExecutionService] Aggregator threshold reached, filtering to 'valid' connections");
                downstreamConnections = downstreamConnections.Where(c => 
                    string.IsNullOrEmpty(c.Label) || 
                    string.Equals(c.Label, "valid", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                // Threshold not reached - don't trigger any downstream
                Console.WriteLine($"[WorkflowExecutionService] Aggregator threshold not reached, skipping downstream");
                downstreamConnections = new List<WorkflowConnectionState>();
            }
        }
        

        
        // Apply Custom Node tag filtering - routes based on tags.trigger() calls in scripts
        if (sourceNode?.NodeType.StartsWith("Custom_") == true && 
            outputData.TryGetValue("_TriggeredTags", out var tagsObj))
        {
            List<string>? triggeredTags = null;
            
            // Handle both JSON string (new format) and List<string> (legacy format)
            if (tagsObj is string tagsJson && !string.IsNullOrEmpty(tagsJson))
            {
                try
                {
                    triggeredTags = JsonSerializer.Deserialize<List<string>>(tagsJson);
                }
                catch
                {
                    Console.WriteLine($"[WorkflowExecutionService] Failed to parse _TriggeredTags JSON: {tagsJson}");
                }
            }
            else if (tagsObj is List<string> tagsList)
            {
                triggeredTags = tagsList;
            }
            
            if (triggeredTags != null && triggeredTags.Any())
            {
                // Build detailed connection info for expandable view
                var connectionDetails = new List<string>();
                foreach (var c in downstreamConnections)
                {
                    var targetNode = state.Nodes.FirstOrDefault(n => n.Id == c.TargetId);
                    var willKeep = string.IsNullOrEmpty(c.Label) || 
                        triggeredTags.Any(t => string.Equals(t, c.Label, StringComparison.OrdinalIgnoreCase));
                    connectionDetails.Add($"→ '{targetNode?.Name ?? c.TargetId.ToString()}' label='{c.Label ?? "(none)"}' route={willKeep}");
                }
                
                // Log to node Activity Logs at Info level with detail for looking glass
                _executionManager.AddNodeLog(nodeId, NodeLogLevel.Info, 
                    $"Routing: tags=[{string.Join(", ", triggeredTags)}], {downstreamConnections.Count} connections",
                    string.Join("\n", connectionDetails));
                
                downstreamConnections = downstreamConnections.Where(c => 
                    string.IsNullOrEmpty(c.Label) || 
                    triggeredTags.Any(t => string.Equals(t, c.Label, StringComparison.OrdinalIgnoreCase))).ToList();
                    
                _executionManager.AddNodeLog(nodeId, NodeLogLevel.Info, 
                    $"Routing result: {downstreamConnections.Count} connections will execute",
                    $"Filtered from original {connectionDetails.Count} connections based on tags: [{string.Join(", ", triggeredTags)}]");
            }
        }
        
        Console.WriteLine($"[WorkflowExecutionService] TriggerDownstream: Found {downstreamConnections.Count} downstream connections for node {nodeId}");
        
        foreach (var conn in downstreamConnections)
        {
            // Fire animation event - connection traversal started
            OnConnectionTraversalStarted?.Invoke(conn.Id);
            
            // "no-wait" connections are fire-and-forget - don't await, let the branch run independently
            if (string.Equals(conn.Label, "no-wait", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[WorkflowExecutionService] Executing downstream node {conn.TargetId} (no-wait/fire-and-forget)");
                var capturedConnId = conn.Id;
                var capturedTargetId = conn.TargetId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteNodeInternalAsync(workflowId, capturedTargetId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WorkflowExecutionService] Error in no-wait branch: {ex.Message}");
                    }
                    finally
                    {
                        OnConnectionTraversalEnded?.Invoke(capturedConnId);
                    }
                });
            }
            else
            {
                Console.WriteLine($"[WorkflowExecutionService] Executing downstream node {conn.TargetId}");
                await ExecuteNodeInternalAsync(workflowId, conn.TargetId);
                
                // Fire animation event - connection traversal ended
                OnConnectionTraversalEnded?.Invoke(conn.Id);
            }
        }
    }

    /// <summary>
    /// Recursively collect output data from all upstream nodes in the chain
    /// </summary>
    private void CollectUpstreamData(Guid nodeId, WorkflowRunState state, Dictionary<string, object?> inputData, HashSet<Guid> visitedNodes)
    {
        // Get incoming connections, excluding run:* connections which are special data-only links
        // that shouldn't affect the execution flow (RemoteCommand manages its own data path)
        var incomingConnections = state.Connections
            .Where(c => c.TargetId == nodeId)
            .Where(c => !(c.Label?.StartsWith("run:", StringComparison.OrdinalIgnoreCase) == true))
            .ToList();
        
        foreach (var conn in incomingConnections)
        {
            if (visitedNodes.Contains(conn.SourceId))
                continue; // Prevent infinite loops in cyclic graphs
                
            visitedNodes.Add(conn.SourceId);
            
            var sourceNode = state.Nodes.FirstOrDefault(n => n.Id == conn.SourceId);
            if (sourceNode != null)
            {
                // First, recursively collect from upstream of this source (to get the full chain)
                CollectUpstreamData(conn.SourceId, state, inputData, visitedNodes);
                
                // Then add this node's outputs (later nodes override earlier ones)
                // CRITICAL: Skip _SourceNodeId - it's execution-specific metadata that shouldn't be 
                // accumulated from previous executions. Each execution should use its own trigger's ID.
                foreach (var output in sourceNode.OutputData.Where(o => o.Key != "_SourceNodeId"))
                {
                    // Add both prefixed and raw keys for compatibility
                    inputData[$"{sourceNode.Name}.{output.Key}"] = output.Value;
                    inputData[output.Key] = output.Value;
                }
            }
        }
    }

    /// <summary>
    /// Check if a node is downstream of a specific trigger node by traversing connections
    /// </summary>
    private bool IsNodeDownstreamOfTrigger(Guid nodeId, Guid triggerId, WorkflowRunState state)
    {
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(triggerId);
        visited.Add(triggerId);
        
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            
            // Get all outgoing connections from current node
            var outgoing = state.Connections.Where(c => c.SourceId == currentId);
            foreach (var conn in outgoing)
            {
                if (conn.TargetId == nodeId)
                    return true; // Found!
                    
                if (!visited.Contains(conn.TargetId))
                {
                    visited.Add(conn.TargetId);
                    queue.Enqueue(conn.TargetId);
                }
            }
        }
        
        return false;
    }

    /// <summary>
    /// Resolve configuration placeholders with output from previous nodes
    /// Supports nested JSON paths like {{NodeName.Body.property.nestedProperty}}
    /// </summary>
    private string? ResolveConfigPlaceholders(string? config, Guid currentNodeId, WorkflowRunState state)
    {
        if (string.IsNullOrEmpty(config)) return config;
        
        var result = config;
        
        // Use regex to find all placeholders
        var placeholderRegex = new System.Text.RegularExpressions.Regex(@"\{\{([^}]+)\}\}");
        var matches = placeholderRegex.Matches(config);
        
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var fullPlaceholder = match.Value; // e.g., {{Request.Body.access_token}}
            var key = match.Groups[1].Value;   // e.g., Request.Body.access_token
            
            var parts = key.Split('.', 2); // Split into NodeName and rest
            if (parts.Length < 2) continue;
            
            var sourceNodeName = parts[0];
            var propertyPath = parts[1]; // e.g., Body.access_token or just Body
            
            // Find the source node by name
            var sourceNode = state.Nodes.FirstOrDefault(n => n.Name == sourceNodeName);
            if (sourceNode == null) continue;
            
            // Split the property path to check for JSON path
            var pathParts = propertyPath.Split('.', 2);
            var outputPropertyName = pathParts[0]; // e.g., Body
            var jsonPath = pathParts.Length > 1 ? pathParts[1] : null; // e.g., access_token or null
            
            object? val = null;
            
            // For Cache nodes, read directly from CacheStorageService to get real-time values
            // This is critical for reader connections where the cache may have been updated
            // by another branch of execution in the same workflow run
            if (sourceNode.NodeType == "Cache")
            {
                // Try to get the property directly from cache storage
                var cacheVal = _cacheStorageService.Get(state.WorkflowId, sourceNode.Id, outputPropertyName);
                if (cacheVal != null)
                {
                    val = cacheVal;
                }
                else
                {
                    // Fall back to OutputData if not in cache
                    sourceNode.OutputData.TryGetValue(outputPropertyName, out val);
                }
            }
            else
            {
                // For other nodes, use OutputData as before
                if (!sourceNode.OutputData.TryGetValue(outputPropertyName, out val)) continue;
            }
            
            if (val == null) continue;
            
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
                result = result.Replace(fullPlaceholder, JsonEscape(resolvedValue));
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Extract a value from JSON using a dot-notation path
    /// </summary>
    private string? ExtractJsonPath(string json, string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var element = doc.RootElement;
            
            // Navigate through the path
            foreach (var part in path.Split('.'))
            {
                if (element.ValueKind == System.Text.Json.JsonValueKind.Object && element.TryGetProperty(part, out var child))
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
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number => element.GetRawText(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                System.Text.Json.JsonValueKind.Null => "",
                _ => element.GetRawText() // For objects/arrays, return raw JSON
            };
        }
        catch
        {
            return null; // Invalid JSON or path
        }
    }

    private string JsonEscape(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }

    /// <summary>
    /// Merges only cost/token fields from the updated config back into the original config.
    /// This preserves prompt placeholders while updating Cost, InputTokens, and OutputTokens.
    /// </summary>
    private string MergeAiCostUpdates(string? originalConfigJson, string? updatedConfigJson, string nodeType)
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
            Console.WriteLine($"[WorkflowExecutionService] Error merging AI config updates: {ex.Message}");
            return originalConfigJson ?? "{}";
        }
    }

    /// <summary>
    /// Stop tracking a workflow
    /// </summary>
    public async Task StopWorkflowAsync(Guid workflowId)
    {
        // Get the workflow state before removing to clean up node-specific resources
        if (_runningWorkflows.TryRemove(workflowId, out var state))
        {
            // Stop any Scheduler node timers
            foreach (var node in state.Nodes.Where(n => n.NodeType == "Scheduler"))
            {
                Nodes.SchedulerNode.StopTimer(node.Id);
            }
            
            // Clear Aggregator node buffers to prevent memory leaks
            Nodes.AggregatorNode.ClearWorkflowBuffers(workflowId);
        }

        // Update database
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var execution = await context.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.WorkflowId == workflowId);

        if (execution != null)
        {
            execution.Status = WorkflowExecutionStatus.Stopped;
            execution.StoppedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }
    
    /// <summary>
    /// Stop all running workflows for a user (called when execution limit is reached)
    /// </summary>
    public async Task StopAllUserWorkflowsAsync(string userId, string reason)
    {
        Console.WriteLine($"[WorkflowExecutionService] Stopping all workflows for user {userId}: {reason}");
        
        // Get all workflow IDs for this user
        var userWorkflowIds = _runningWorkflows.Values
            .Where(w => w.UserId == userId)
            .Select(w => w.WorkflowId)
            .ToList();
        
        foreach (var workflowId in userWorkflowIds)
        {
            _runningWorkflows.TryRemove(workflowId, out _);
        }
        
        // Update database for all user's running executions
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var executions = await context.WorkflowExecutions
            .Where(e => e.UserId == userId && e.Status == WorkflowExecutionStatus.Running)
            .ToListAsync();
        
        foreach (var execution in executions)
        {
            execution.Status = WorkflowExecutionStatus.Stopped;
            execution.StoppedAt = DateTime.UtcNow;
        }
        
        await context.SaveChangesAsync();
        
        Console.WriteLine($"[WorkflowExecutionService] Stopped {userWorkflowIds.Count} in-memory and {executions.Count} persisted workflows for user {userId}");
    }

    /// <summary>
    /// Increment the execution count for a workflow
    /// </summary>
    public async Task IncrementExecutionCountAsync(Guid workflowId)
    {
        if (_runningWorkflows.TryGetValue(workflowId, out var state))
        {
            state.ExecutionCount++;
            state.LastExecutedAt = DateTime.UtcNow;
        }

        // Update database
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var execution = await context.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.WorkflowId == workflowId);

        if (execution != null)
        {
            execution.ExecutionCount++;
            execution.LastExecutedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Check if a workflow is running
    /// </summary>
    public bool IsRunning(Guid workflowId) => _runningWorkflows.ContainsKey(workflowId);

    /// <summary>
    /// Get all running workflows for a user
    /// </summary>
    public List<RunningWorkflowInfo> GetRunningWorkflows(string userId)
    {
        return _runningWorkflows.Values
            .Where(w => w.UserId == userId)
            .Select(w => new RunningWorkflowInfo
            {
                WorkflowId = w.WorkflowId,
                WorkflowName = w.WorkflowName,
                OrganizationId = w.OrganizationId,
                StartedAt = w.StartedAt,
                ExecutionCount = w.ExecutionCount,
                LastExecutedAt = w.LastExecutedAt
            })
            .ToList();
    }

    /// <summary>
    /// Get all running workflows (for dashboard)
    /// </summary>
    public async Task<List<RunningWorkflowInfo>> GetAllRunningWorkflowsAsync(string userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var executions = await context.WorkflowExecutions
            .Include(e => e.Workflow)
            .Where(e => e.UserId == userId && e.Status == WorkflowExecutionStatus.Running)
            .ToListAsync();

        return executions.Select(e => new RunningWorkflowInfo
        {
            WorkflowId = e.WorkflowId,
            WorkflowName = e.Workflow?.Name ?? "Unknown",
            StartedAt = e.StartedAt ?? DateTime.UtcNow,
            ExecutionCount = e.ExecutionCount,
            LastExecutedAt = e.LastExecutedAt
        }).ToList();
    }

    public void Dispose()
    {
        _executionManager.OnNodeOutputDataReceived -= HandleNodeOutputDataReceived;
        _executionManager.OnNodeLogAdded -= HandleNodeLogAdded;
        _runningWorkflows.Clear();
    }
}

/// <summary>
/// Info about a running workflow for display
/// </summary>
public class RunningWorkflowInfo
{
    public Guid WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public Guid? OrganizationId { get; set; }
    public DateTime StartedAt { get; set; }
    public int ExecutionCount { get; set; }
    public DateTime? LastExecutedAt { get; set; }
}
