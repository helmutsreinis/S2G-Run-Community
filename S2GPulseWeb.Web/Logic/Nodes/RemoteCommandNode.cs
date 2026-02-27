using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Remote Command node executor - sends commands to multiple Remote Machine nodes
/// and aggregates results with configurable timeout.
/// </summary>
public class RemoteCommandNode : BaseNodeExecutor
{
    public RemoteCommandNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "RemoteCommand";

    public override List<string> GetOutputParameters() => new()
    {
        "Results", "ResultsJson", "SuccessCount", "TimeoutCount", "TotalCount"
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<RemoteCommandNodeConfig>(node.Configuration ?? "{}") ?? new();
        
        // Resolve placeholders in command
        var command = ResolvePlaceholders(config.Command ?? "", inputData);
        
        if (string.IsNullOrWhiteSpace(command))
        {
            Log(node, NodeLogLevel.Warning, "No command specified", "Command field is empty");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "No command specified",
                OutputData = new() { ["Results"] = "[]", ["ResultsJson"] = "[]" }
            };
        }

        // Get connected Remote nodes from workflow context
        var connectedRemotes = DiscoverConnectedRemoteNodes(node, inputData);
        
        if (!connectedRemotes.Any())
        {
            Log(node, NodeLogLevel.Warning, "No Remote nodes connected", 
                "Connect this node to Remote Machine nodes with 'run:rm-*' labeled connections");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "No Remote nodes connected",
                OutputData = new() { ["Results"] = "[]", ["ResultsJson"] = "[]" }
            };
        }

        // Filter by target connection tags if specified
        var targets = connectedRemotes;
        if (config.TargetConnectionTags?.Any() == true)
        {
            targets = connectedRemotes
                .Where(r => config.TargetConnectionTags.Contains(r.ConnectionTag))
                .ToList();
            
            if (!targets.Any())
            {
                Log(node, NodeLogLevel.Warning, "No matching targets", 
                    $"No Remote nodes match tags: {string.Join(", ", config.TargetConnectionTags)}");
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"No Remote nodes match specified tags",
                    OutputData = new() { ["Results"] = "[]", ["ResultsJson"] = "[]" }
                };
            }
        }

        Log(node, NodeLogLevel.Info, "Executing command on remotes", 
            $"Command: {Truncate(command, 50)}, Targets: {targets.Count}");

        // Queue commands to each target
        var pendingExecutions = new List<(ConnectedRemote Remote, Guid ExecutionId)>();
        foreach (var target in targets)
        {
            var executionId = RemoteNode.QueueCommandExternal(
                target.RemoteNodeId, 
                command, 
                config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 60);
            
            pendingExecutions.Add((target, executionId));
            Log(node, NodeLogLevel.Debug, "Command queued", 
                $"Remote: {target.RemoteNodeName}, Tag: {target.ConnectionTag}, ExecutionId: {executionId}");
        }

        // Poll for results with timeout
        var timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 60);
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var startTime = DateTime.UtcNow;
        var results = new List<RemoteCommandResult>();

        while (DateTime.UtcNow - startTime < timeout)
        {
            var allComplete = true;
            
            foreach (var (remote, executionId) in pendingExecutions)
            {
                // Skip if already processed
                if (results.Any(r => r.ExecutionId == executionId.ToString()))
                    continue;

                var commandResult = RemoteNode.GetCommandResultExternal(remote.RemoteNodeId, executionId);
                
                if (commandResult != null && commandResult.CompletedAt.HasValue)
                {
                    // Command completed
                    var output = commandResult.Output ?? "";
                    bool? validJson = null;
                    object? executionResult = null;

                    // Try to parse as JSON
                    if (!string.IsNullOrEmpty(output))
                    {
                        try
                        {
                            executionResult = JsonSerializer.Deserialize<JsonElement>(output);
                            validJson = true;
                        }
                        catch
                        {
                            executionResult = output;
                            validJson = false;
                        }
                    }

                    results.Add(new RemoteCommandResult
                    {
                        RemoteMachine = remote.RemoteNodeName,
                        ConnectionTag = remote.ConnectionTag,
                        ExecutionId = executionId.ToString(),
                        ExecutionTimeout = false,
                        ValidJson = validJson,
                        ExecutionResult = executionResult
                    });

                    Log(node, NodeLogLevel.Info, "Result received", 
                        $"Remote: {remote.RemoteNodeName}, ExitCode: {commandResult.ExitCode}");
                }
                else
                {
                    allComplete = false;
                }
            }

            if (allComplete || results.Count == pendingExecutions.Count)
                break;

            await Task.Delay(pollInterval);
        }

        // Mark remaining as timed out
        foreach (var (remote, executionId) in pendingExecutions)
        {
            if (!results.Any(r => r.ExecutionId == executionId.ToString()))
            {
                results.Add(new RemoteCommandResult
                {
                    RemoteMachine = remote.RemoteNodeName,
                    ConnectionTag = remote.ConnectionTag,
                    ExecutionId = executionId.ToString(),
                    ExecutionTimeout = true,
                    ValidJson = null,
                    ExecutionResult = null
                });

                Log(node, NodeLogLevel.Warning, "Execution timeout", 
                    $"Remote: {remote.RemoteNodeName}, Tag: {remote.ConnectionTag}");
            }
        }

        var successCount = results.Count(r => !r.ExecutionTimeout);
        var timeoutCount = results.Count(r => r.ExecutionTimeout);
        var resultsJson = JsonSerializer.Serialize(results, new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false 
        });

        Log(node, NodeLogLevel.Info, "Execution complete", 
            $"Success: {successCount}, Timeout: {timeoutCount}, Total: {results.Count}");

        return new NodeExecutionResult
        {
            Success = successCount > 0,
            OutputData = new()
            {
                ["Results"] = results,
                ["ResultsJson"] = resultsJson,
                ["SuccessCount"] = successCount,
                ["TimeoutCount"] = timeoutCount,
                ["TotalCount"] = results.Count
            }
        };
    }

    /// <summary>
    /// Discovers Remote nodes connected via 'run:rm-*' labeled connections.
    /// </summary>
    private List<ConnectedRemote> DiscoverConnectedRemoteNodes(
        WorkflowNode node, 
        Dictionary<string, object?> inputData)
    {
        var connectedRemotes = new List<ConnectedRemote>();

        // Get connections from workflow context (injected by Designer during execution)
        if (!inputData.TryGetValue("_Connections", out var connectionsObj) || 
            connectionsObj is not List<(Guid Id, Guid SourceId, Guid TargetId, string? Label)> connections)
        {
            // Fallback: check if configuration has pre-discovered connections
            var config = JsonSerializer.Deserialize<RemoteCommandNodeConfig>(node.Configuration ?? "{}");
            if (config?.ConnectedRemotes != null)
            {
                return config.ConnectedRemotes;
            }
            return connectedRemotes;
        }

        // Get canvas nodes for name resolution
        if (!inputData.TryGetValue("_CanvasNodes", out var nodesObj))
            return connectedRemotes;

        // Find connections where this node is source and label starts with "run:rm-"
        var runConnections = connections
            .Where(c => c.SourceId == node.Id && 
                       c.Label?.StartsWith("run:rm-", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        foreach (var conn in runConnections)
        {
            var tag = conn.Label!.Replace("run:", ""); // Extract "rm-01" from "run:rm-01"
            
            // Find target node name
            string nodeName = "Unknown";
            if (nodesObj is IEnumerable<dynamic> nodes)
            {
                var targetNode = nodes.FirstOrDefault(n => n.Id == conn.TargetId);
                if (targetNode != null)
                {
                    nodeName = targetNode.Name ?? "Remote";
                }
            }

            connectedRemotes.Add(new ConnectedRemote
            {
                RemoteNodeId = conn.TargetId,
                RemoteNodeName = nodeName,
                ConnectionTag = tag
            });
        }

        return connectedRemotes;
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
            
            return match.Value;
        });
        
        return result;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}

#region Configuration Models

public class RemoteCommandNodeConfig
{
    /// <summary>
    /// Shell command to execute on remote machines.
    /// </summary>
    public string? Command { get; set; }
    
    /// <summary>
    /// List of connection tags to execute on (e.g., ["rm-01", "rm-02"]).
    /// If empty, executes on all connected remotes.
    /// </summary>
    public List<string>? TargetConnectionTags { get; set; }
    
    /// <summary>
    /// Maximum time to wait for all results (seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
    
    /// <summary>
    /// Pre-discovered connected remotes (populated by editor).
    /// </summary>
    public List<ConnectedRemote>? ConnectedRemotes { get; set; }
}

public class ConnectedRemote
{
    public Guid RemoteNodeId { get; set; }
    public string RemoteNodeName { get; set; } = "";
    public string ConnectionTag { get; set; } = "";
}

public class RemoteCommandResult
{
    public string RemoteMachine { get; set; } = "";
    public string ConnectionTag { get; set; } = "";
    public string ExecutionId { get; set; } = "";
    public bool ExecutionTimeout { get; set; }
    public bool? ValidJson { get; set; }
    public object? ExecutionResult { get; set; }
}

#endregion
