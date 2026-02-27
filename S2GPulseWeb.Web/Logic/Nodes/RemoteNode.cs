using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Remote node executor - manages command queue for remote Linux/Windows clients.
/// Clients connect via HttpListener proxy to fetch commands and submit output.
/// </summary>
public class RemoteNode : BaseNodeExecutor
{
    // Static registries for command queues and client metadata
    private static readonly ConcurrentDictionary<Guid, List<RemoteCommand>> _commandQueues = new();
    private static readonly ConcurrentDictionary<string, RemoteClientMetadata> _clientMetadata = new();

    public RemoteNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Remote";

    public override List<string> GetOutputParameters() => new()
    {
        "CommandOutput", "ExitCode", "ExecutionId", "ClientId",
        "Hostname", "OS", "CpuUsage", "MemoryUsage", "DiskUsage",
        "LastSeen", "IsOnline", "QueuedCommands", "Response"
    };

    protected override Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<RemoteNodeConfig>(node.Configuration ?? "{}") ?? new();

        // Auto-generate ClientId if not set
        if (string.IsNullOrEmpty(config.ClientId))
        {
            config.ClientId = Guid.NewGuid().ToString();
            node.Configuration = JsonSerializer.Serialize(config);
            Log(node, NodeLogLevel.Info, "Generated new ClientId", config.ClientId);
        }

        // Initialize queue for this node if not exists
        _commandQueues.TryAdd(node.Id, new List<RemoteCommand>());

        // Clean expired commands
        CleanExpiredCommands(node.Id);

        // Determine operation from incoming request body
        // First try direct keys, then parse from Body JSON (for data coming through HttpListener)
        var action = GetInputValue<string>(inputData, "action") ?? "";
        var clientId = GetInputValue<string>(inputData, "clientId") ?? "";
        
        // If action/clientId not found directly, try parsing from Body JSON
        if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(clientId))
        {
            var bodyJson = GetInputValue<string>(inputData, "Body") ?? "";
            if (!string.IsNullOrEmpty(bodyJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(bodyJson);
                    var root = doc.RootElement;
                    
                    if (string.IsNullOrEmpty(action) && root.TryGetProperty("action", out var actionProp))
                    {
                        action = actionProp.GetString() ?? "";
                    }
                    if (string.IsNullOrEmpty(clientId) && root.TryGetProperty("clientId", out var clientIdProp))
                    {
                        clientId = clientIdProp.GetString() ?? "";
                    }
                    
                    // Also extract other fields from body for submit/heartbeat actions
                    if (action.ToLowerInvariant() is "submit" or "heartbeat")
                    {
                        // Merge body fields into inputData for downstream handlers
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (!inputData.ContainsKey(prop.Name))
                            {
                                // Debug log for customReports
                                if (prop.Name == "customReports")
                                {
                                    Log(node, NodeLogLevel.Debug, "Body parsing customReports", 
                                        $"ValueKind={prop.Value.ValueKind}, RawLen={prop.Value.GetRawText().Length}");
                                }
                                
                                inputData[prop.Name] = prop.Value.ValueKind switch
                                {
                                    JsonValueKind.String => prop.Value.GetString(),
                                    JsonValueKind.Number => prop.Value.GetDouble(),
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    _ => prop.Value.GetRawText()
                                };
                            }
                        }
                    }
                    
                    Log(node, NodeLogLevel.Debug, "Parsed Body JSON", $"action={action}, clientId={clientId}");
                }
                catch (JsonException ex)
                {
                    Log(node, NodeLogLevel.Warning, "Failed to parse Body JSON", ex.Message);
                }
            }
        }

        // DEBUG: Log all incoming requests to trace execution flow
        Console.WriteLine($"[RemoteNode] InternalExecuteAsync: action='{action}', clientId='{clientId}', hasRequestId={inputData.ContainsKey("RequestId")}, _SourceNodeId={GetInputValue<Guid?>(inputData, "_SourceNodeId")}");

        // Validate client ID for client operations (fetch, submit, heartbeat)
        var actionLower = action.ToLowerInvariant();
        var isClientOperation = actionLower is "fetch" or "submit" or "heartbeat";
        
        if (isClientOperation && !string.IsNullOrEmpty(clientId) && clientId != config.ClientId)
        {
            Log(node, NodeLogLevel.Warning, "Invalid ClientId", $"Expected: {config.ClientId}, Got: {clientId}");
            return Task.FromResult(new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Invalid ClientId",
                OutputData = new() { ["Response"] = JsonSerializer.Serialize(new { error = "Invalid ClientId" }) }
            });
        }

        // Apply SourceListenerId filter for ALL operations in multi-listener workflows
        // This ensures the Remote node only processes requests from its designated listener
        if (config.SourceListenerId.HasValue)
        {
            var sourceNodeId = GetInputValue<Guid?>(inputData, "_SourceNodeId");
            
            if (sourceNodeId.HasValue && sourceNodeId.Value != config.SourceListenerId.Value)
            {
                Log(node, NodeLogLevel.Debug, "Skipping - wrong source listener", 
                    $"Expected: {config.SourceListenerId}, Got: {sourceNodeId}");
                return Task.FromResult(new NodeExecutionResult
                {
                    Success = true,
                    OutputData = new() { ["_Skipped"] = true }
                });
            }
        }

        // For client operations (fetch, submit, heartbeat), process immediately
        // These come directly from the remote client polling/submitting
        // Pass inputData so handlers can directly emit HTTP response using RequestId
        if (isClientOperation)
        {
            return actionLower switch
            {
                "fetch" => HandleFetch(node, config, inputData, clientId),
                "submit" => HandleSubmit(node, config, inputData, clientId),
                "heartbeat" => HandleHeartbeat(node, config, inputData, clientId),
                _ => throw new InvalidOperationException($"Unknown client operation: {actionLower}")
            };
        }

        // Default: queue a new command from workflow
        return HandleQueueCommand(node, config, inputData);
    }

    /// <summary>
    /// Handle heartbeat request - update client metadata without command execution
    /// </summary>
    private Task<NodeExecutionResult> HandleHeartbeat(
        WorkflowNode node,
        RemoteNodeConfig config,
        Dictionary<string, object?> inputData,
        string clientId)
    {
        // Extract system metadata
        var hostname = GetInputValue<string>(inputData, "hostname") ?? "";
        var os = GetInputValue<string>(inputData, "os") ?? "";
        var cpuStr = GetInputValue<string>(inputData, "cpu") ?? "0";
        var memoryStr = GetInputValue<string>(inputData, "memory") ?? "0";
        var diskStr = GetInputValue<string>(inputData, "disk") ?? "0";
        var diskBreakdown = GetInputValue<string>(inputData, "diskBreakdown") ?? "";
        var customReports = GetInputValue<string>(inputData, "customReports") ?? "";
        
        // Handle potential double-encoding from PowerShell (JSON string wrapped in JSON string)
        if (customReports.StartsWith("\"") && customReports.EndsWith("\""))
        {
            try
            {
                customReports = JsonSerializer.Deserialize<string>(customReports) ?? "";
            }
            catch { }
        }

        double.TryParse(cpuStr, out var cpu);
        double.TryParse(memoryStr, out var memory);
        double.TryParse(diskStr, out var disk);

        // Update client metadata
        UpdateClientMetadata(node.Id, clientId, new RemoteClientMetadata
        {
            ClientId = clientId,
            Hostname = hostname,
            OS = os,
            CpuUsage = cpu,
            MemoryUsage = memory,
            DiskUsage = disk,
            DiskBreakdown = diskBreakdown,
            CustomReports = customReports,
            LastSeen = DateTime.UtcNow
        });

        var reportCount = string.IsNullOrEmpty(customReports) || customReports == "[]" ? 0 : 
            customReports.Count(c => c == '{');
        var reportInfo = reportCount > 0 ? $", Reports: {reportCount}" : "";
        Log(node, NodeLogLevel.Debug, "Heartbeat received", $"Client {clientId} CPU: {cpu:F1}%, Mem: {memory:F1}%, Disk: {disk:F1}%{reportInfo}");

        // Directly emit HTTP response
        var heartbeatResponse = JsonSerializer.Serialize(new { status = "ok", received = DateTime.UtcNow });
        EmitDirectResponse(inputData, heartbeatResponse);

        return Task.FromResult(new NodeExecutionResult
        {
            Success = true,
            OutputData = new()
            {
                ["Response"] = heartbeatResponse,
                ["ClientId"] = clientId,
                ["IsOnline"] = true,
                // Include metadata for surface fields
                ["OS"] = os,
                ["Hostname"] = hostname,
                ["CpuUsage"] = $"{cpu:F1}%",
                ["MemoryUsage"] = $"{memory:F1}%",
                ["DiskUsage"] = $"{disk:F1}%",
                ["LastSeen"] = DateTime.UtcNow.ToString("HH:mm:ss")
            }
        });
    }

    /// <summary>
    /// Handle fetch request - return pending commands for the client
    /// </summary>
    private Task<NodeExecutionResult> HandleFetch(WorkflowNode node, RemoteNodeConfig config, Dictionary<string, object?> inputData, string clientId)
    {
        var queue = _commandQueues.GetOrAdd(node.Id, _ => new List<RemoteCommand>());

        List<RemoteCommand> pendingCommands;
        lock (queue)
        {
            pendingCommands = queue.Where(c => !c.Dispatched).ToList();
            foreach (var cmd in pendingCommands)
            {
                cmd.Dispatched = true;
                cmd.DispatchedAt = DateTime.UtcNow;
            }
        }

        // Update client last seen
        UpdateClientMetadata(node.Id, clientId, null);

        var commandsJson = JsonSerializer.Serialize(pendingCommands.Select(c => new
        {
            executionId = c.ExecutionId,
            command = c.Command,
            timeoutSeconds = c.TimeoutSeconds
        }));

        Log(node, NodeLogLevel.Info, "Fetch request", $"Returned {pendingCommands.Count} command(s) to client {clientId}");

        // Directly emit HTTP response if RequestId is available (for proxy requests)
        // This bypasses the need for a separate HttpResponse node in the workflow
        EmitDirectResponse(inputData, commandsJson);

        return Task.FromResult(new NodeExecutionResult
        {
            Success = true,
            OutputData = new()
            {
                ["Response"] = commandsJson,
                ["QueuedCommands"] = pendingCommands.Count,
                ["ClientId"] = clientId,
                ["IsOnline"] = true
            }
        });
    }

    /// <summary>
    /// Handle submit request - receive command output from client
    /// </summary>
    private Task<NodeExecutionResult> HandleSubmit(
        WorkflowNode node,
        RemoteNodeConfig config,
        Dictionary<string, object?> inputData,
        string clientId)
    {
        var executionId = GetInputValue<string>(inputData, "executionId") ?? "";
        var output = GetInputValue<string>(inputData, "output") ?? "";
        var exitCodeStr = GetInputValue<string>(inputData, "exitCode") ?? "0";
        int.TryParse(exitCodeStr, out var exitCode);

        // Extract system metadata
        var hostname = GetInputValue<string>(inputData, "hostname") ?? "";
        var os = GetInputValue<string>(inputData, "os") ?? "";
        var cpuStr = GetInputValue<string>(inputData, "cpu") ?? "0";
        var memoryStr = GetInputValue<string>(inputData, "memory") ?? "0";
        var diskStr = GetInputValue<string>(inputData, "disk") ?? "0";

        double.TryParse(cpuStr, out var cpu);
        double.TryParse(memoryStr, out var memory);
        double.TryParse(diskStr, out var disk);

        // Update client metadata
        UpdateClientMetadata(node.Id, clientId, new RemoteClientMetadata
        {
            ClientId = clientId,
            Hostname = hostname,
            OS = os,
            CpuUsage = cpu,
            MemoryUsage = memory,
            DiskUsage = disk,
            LastSeen = DateTime.UtcNow
        });

        // Find and update the command
        var queue = _commandQueues.GetOrAdd(node.Id, _ => new List<RemoteCommand>());
        RemoteCommand? matchedCommand = null;
        lock (queue)
        {
            matchedCommand = queue.FirstOrDefault(c => c.ExecutionId.ToString() == executionId);
            if (matchedCommand != null)
            {
                matchedCommand.Output = output;
                matchedCommand.ExitCode = exitCode;
                matchedCommand.CompletedAt = DateTime.UtcNow;
            }
        }

        if (matchedCommand == null)
        {
            Log(node, NodeLogLevel.Warning, "Unknown executionId", executionId);
        }
        else
        {
            Log(node, NodeLogLevel.Info, "Command output received",
                $"ExecutionId: {executionId}, ExitCode: {exitCode}, Output length: {output.Length}");
        }

        var responseJson = JsonSerializer.Serialize(new { success = true, received = executionId });
        
        // Directly emit HTTP response
        EmitDirectResponse(inputData, responseJson);

        return Task.FromResult(new NodeExecutionResult
        {
            Success = true,
            OutputData = new()
            {
                ["Response"] = responseJson,
                ["CommandOutput"] = output,
                ["ExitCode"] = exitCode,
                ["ExecutionId"] = executionId,
                ["ClientId"] = clientId,
                ["Hostname"] = hostname,
                ["OS"] = os,
                ["CpuUsage"] = cpu,
                ["MemoryUsage"] = memory,
                ["DiskUsage"] = disk,
                ["LastSeen"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                ["IsOnline"] = true
            }
        });
    }

    /// <summary>
    /// Handle queue command - add a new command to the queue (triggered from workflow)
    /// </summary>
    private Task<NodeExecutionResult> HandleQueueCommand(
        WorkflowNode node,
        RemoteNodeConfig config,
        Dictionary<string, object?> inputData)
    {
        // Resolve placeholders in command
        var command = ResolvePlaceholders(config.Command ?? "", inputData);

        if (string.IsNullOrWhiteSpace(command))
        {
            // No command to queue - return current status
            var metadata = GetClientMetadata(node.Id, config.ClientId ?? "");
            return Task.FromResult(new NodeExecutionResult
            {
                Success = true,
                OutputData = BuildStatusOutput(node.Id, config.ClientId ?? "", metadata)
            });
        }

        var executionId = Guid.NewGuid();
        var newCommand = new RemoteCommand
        {
            ExecutionId = executionId,
            Command = command,
            TimeoutSeconds = config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 60,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(config.ExpirationMinutes > 0 ? config.ExpirationMinutes : 5)
        };

        var queue = _commandQueues.GetOrAdd(node.Id, _ => new List<RemoteCommand>());
        lock (queue)
        {
            queue.Add(newCommand);
        }

        Log(node, NodeLogLevel.Info, "Command queued",
            $"ExecutionId: {executionId}, Command: {command.Substring(0, Math.Min(50, command.Length))}...");

        return Task.FromResult(new NodeExecutionResult
        {
            Success = true,
            OutputData = new()
            {
                ["ExecutionId"] = executionId.ToString(),
                ["QueuedCommands"] = queue.Count,
                ["ClientId"] = config.ClientId
            }
        });
    }

    private void CleanExpiredCommands(Guid nodeId)
    {
        if (_commandQueues.TryGetValue(nodeId, out var queue))
        {
            lock (queue)
            {
                var now = DateTime.UtcNow;
                queue.RemoveAll(c => c.ExpiresAt < now);
            }
        }
    }

    private void UpdateClientMetadata(Guid nodeId, string clientId, RemoteClientMetadata? newMetadata)
    {
        var key = $"{nodeId}_{clientId}";
        
        if (_clientMetadata.TryGetValue(key, out var existing))
        {
            if (newMetadata != null)
            {
                // Update current values
                existing.Hostname = newMetadata.Hostname;
                existing.OS = newMetadata.OS;
                existing.CpuUsage = newMetadata.CpuUsage;
                existing.MemoryUsage = newMetadata.MemoryUsage;
                existing.DiskUsage = newMetadata.DiskUsage;
                existing.DiskBreakdown = newMetadata.DiskBreakdown;
                existing.CustomReports = newMetadata.CustomReports;
                existing.LastSeen = DateTime.UtcNow;
                
                // Add to history (sample every 5 minutes max)
                var lastSample = existing.MetricsHistory.LastOrDefault();
                if (lastSample == null || (DateTime.UtcNow - lastSample.Timestamp).TotalMinutes >= 5)
                {
                    existing.MetricsHistory.Add(new MetricsSample
                    {
                        Timestamp = DateTime.UtcNow,
                        CpuUsage = newMetadata.CpuUsage,
                        MemoryUsage = newMetadata.MemoryUsage,
                        DiskUsage = newMetadata.DiskUsage
                    });
                    
                    // Trim to max samples (24 hours)
                    while (existing.MetricsHistory.Count > RemoteClientMetadata.MaxHistorySamples)
                    {
                        existing.MetricsHistory.RemoveAt(0);
                    }
                }
            }
            else
            {
                existing.LastSeen = DateTime.UtcNow;
            }
        }
        else if (newMetadata != null)
        {
            // First time - initialize with history
            newMetadata.MetricsHistory.Add(new MetricsSample
            {
                Timestamp = DateTime.UtcNow,
                CpuUsage = newMetadata.CpuUsage,
                MemoryUsage = newMetadata.MemoryUsage,
                DiskUsage = newMetadata.DiskUsage
            });
            _clientMetadata[key] = newMetadata;
        }
        else
        {
            _clientMetadata[key] = new RemoteClientMetadata
            {
                ClientId = clientId,
                LastSeen = DateTime.UtcNow
            };
        }
    }

    private RemoteClientMetadata? GetClientMetadata(Guid nodeId, string clientId)
    {
        var key = $"{nodeId}_{clientId}";
        return _clientMetadata.TryGetValue(key, out var metadata) ? metadata : null;
    }

    private Dictionary<string, object?> BuildStatusOutput(Guid nodeId, string clientId, RemoteClientMetadata? metadata)
    {
        var queueCount = _commandQueues.TryGetValue(nodeId, out var queue) ? queue.Count : 0;
        var isOnline = metadata != null && (DateTime.UtcNow - metadata.LastSeen).TotalMinutes < 2;

        return new()
        {
            ["ClientId"] = clientId,
            ["QueuedCommands"] = queueCount,
            ["Hostname"] = metadata?.Hostname ?? "",
            ["OS"] = metadata?.OS ?? "",
            ["CpuUsage"] = metadata?.CpuUsage ?? 0,
            ["MemoryUsage"] = metadata?.MemoryUsage ?? 0,
            ["DiskUsage"] = metadata?.DiskUsage ?? 0,
            ["LastSeen"] = metadata?.LastSeen.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            ["IsOnline"] = isOnline
        };
    }

    private T? GetInputValue<T>(Dictionary<string, object?> inputData, string key)
    {
        // Check direct key and prefixed versions
        foreach (var inputKey in inputData.Keys)
        {
            if (inputKey.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                inputKey.EndsWith("." + key, StringComparison.OrdinalIgnoreCase))
            {
                var value = inputData[inputKey];
                if (value == null) return default;

                // Handle JsonElement
                if (value is JsonElement jsonElement)
                {
                    if (typeof(T) == typeof(string))
                        return (T)(object)(jsonElement.ToString() ?? "");
                    if (typeof(T) == typeof(int) && jsonElement.TryGetInt32(out var intVal))
                        return (T)(object)intVal;
                    if (typeof(T) == typeof(double) && jsonElement.TryGetDouble(out var doubleVal))
                        return (T)(object)doubleVal;
                    return (T)(object)jsonElement.ToString()!;
                }

                if (value is T typedValue)
                    return typedValue;

                return (T)Convert.ChangeType(value, typeof(T));
            }
        }
        return default;
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

    /// <summary>
    /// Directly emit HTTP response to the client if RequestId is available.
    /// This allows Remote node to respond directly to client requests without needing a separate HttpResponse node.
    /// </summary>
    private void EmitDirectResponse(Dictionary<string, object?> inputData, string responseBody, int statusCode = 200)
    {
        // Try to extract RequestId from input data (passed from HttpListener)
        Guid? requestId = null;
        if (inputData.TryGetValue("RequestId", out var ridObj))
        {
            if (ridObj is Guid g)
                requestId = g;
            else if (ridObj is string s && Guid.TryParse(s, out var parsedGuid))
                requestId = parsedGuid;
            else if (ridObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(je.GetString(), out var jeGuid))
                requestId = jeGuid;
        }

        if (requestId.HasValue)
        {
            var response = new HttpResponseData
            {
                StatusCode = statusCode,
                Body = responseBody,
                ContentType = "application/json"
            };
            Console.WriteLine($"[RemoteNode] EmitDirectResponse: RequestId={requestId}, BodyLen={responseBody.Length}");
            _executionManager.EmitResponse(requestId.Value, response);
        }
        else
        {
            Console.WriteLine($"[RemoteNode] EmitDirectResponse: No RequestId found in inputData. Keys: {string.Join(", ", inputData.Keys.Take(10))}");
        }
    }

    /// <summary>
    /// Get the number of queued commands for a node (for UI display)
    /// </summary>
    public static int GetQueuedCommandCount(Guid nodeId)
    {
        return _commandQueues.TryGetValue(nodeId, out var queue) ? queue.Count : 0;
    }

    /// <summary>
    /// Get client metadata for a node (for UI display)
    /// </summary>
    public static RemoteClientMetadata? GetClientStatus(Guid nodeId, string clientId)
    {
        var key = $"{nodeId}_{clientId}";
        return _clientMetadata.TryGetValue(key, out var metadata) ? metadata : null;
    }

    /// <summary>
    /// Get client metadata for a node by nodeId only (iterates all entries for that node).
    /// Used by RemoteCommandEditor which may not know the exact clientId.
    /// </summary>
    public static RemoteClientMetadata? GetClientStatusByNodeId(Guid nodeId)
    {
        var prefix = $"{nodeId}_";
        var entry = _clientMetadata.FirstOrDefault(kvp => kvp.Key.StartsWith(prefix));
        return entry.Value;
    }

    /// <summary>
    /// Get all commands for a node (for UI display)
    /// </summary>
    public static List<RemoteCommand> GetAllCommands(Guid nodeId)
    {
        if (_commandQueues.TryGetValue(nodeId, out var queue))
        {
            lock (queue)
            {
                return queue.ToList();
            }
        }
        return new List<RemoteCommand>();
    }

    /// <summary>
    /// Queue a command externally from the UI (for RemoteMachineMonitor)
    /// </summary>
    public static Guid QueueCommandExternal(Guid nodeId, string command, int timeoutSeconds, int expirationMinutes = 5)
    {
        var executionId = Guid.NewGuid();
        
        // Encode command as Base64 with __PS64__ prefix for proper PowerShell execution on clients
        // This prevents issues with pipes, special characters, and multi-line commands
        // EXCEPTION: Don't encode __REPORT__ commands - they're internal protocol commands
        var encodedCommand = command.StartsWith("__REPORT__") 
            ? command  // Keep as-is for internal protocol
            : $"__PS64__{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(command))}";
        
        var newCommand = new RemoteCommand
        {
            ExecutionId = executionId,
            Command = encodedCommand,
            TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 60,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expirationMinutes > 0 ? expirationMinutes : 5)
        };

        var queue = _commandQueues.GetOrAdd(nodeId, _ => new List<RemoteCommand>());
        lock (queue)
        {
            queue.Add(newCommand);
        }

        return executionId;
    }

    /// <summary>
    /// Clear all queues (for testing)
    /// </summary>
    public static void ClearAllQueues()
    {
        _commandQueues.Clear();
        _clientMetadata.Clear();
    }

    /// <summary>
    /// Get a specific command result by execution ID (for RemoteCommandNode polling)
    /// </summary>
    public static RemoteCommand? GetCommandResultExternal(Guid nodeId, Guid executionId)
    {
        if (_commandQueues.TryGetValue(nodeId, out var queue))
        {
            lock (queue)
            {
                return queue.FirstOrDefault(c => c.ExecutionId == executionId);
            }
        }
        return null;
    }
}

#region Models

public class RemoteNodeConfig
{
    public string? ClientId { get; set; }
    public string? Command { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int ExpirationMinutes { get; set; } = 5;
    /// <summary>
    /// Optional: ID of the listener node this Remote node should receive requests from.
    /// When set, only requests that came through this listener will be processed.
    /// This is required when a workflow has multiple listeners.
    /// </summary>
    public Guid? SourceListenerId { get; set; }
}

public class RemoteCommand
{
    public Guid ExecutionId { get; set; }
    public string Command { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool Dispatched { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string? Output { get; set; }
    public int? ExitCode { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class RemoteClientMetadata
{
    public string ClientId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string OS { get; set; } = "";
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public double DiskUsage { get; set; }
    /// <summary>
    /// JSON array of per-disk breakdown: [{drive, usedPercent, usedGB, freeGB, totalGB}, ...]
    /// </summary>
    public string DiskBreakdown { get; set; } = "";
    /// <summary>
    /// JSON array of custom reports: [{name, enabled, data, error, lastUpdated}, ...]
    /// </summary>
    public string CustomReports { get; set; } = "";
    public DateTime LastSeen { get; set; }
    
    /// <summary>
    /// Historical metrics for charting (last 24 hours, sampled every 5 minutes max)
    /// </summary>
    public List<MetricsSample> MetricsHistory { get; set; } = new();
    
    public const int MaxHistorySamples = 288; // 24 hours * 12 samples/hour
}

public class MetricsSample
{
    public DateTime Timestamp { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public double DiskUsage { get; set; }
}

#endregion
