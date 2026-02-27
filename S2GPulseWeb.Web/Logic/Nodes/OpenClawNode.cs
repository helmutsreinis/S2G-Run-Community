using System.Text.Json;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// OpenClaw Bridge Node — arms a WebSocket endpoint that OpenClaw connects to.
/// When triggered with upstream data, it forwards the mapped fields to all active clients.
/// </summary>
public class OpenClawNode : BaseNodeExecutor
{
    private readonly OpenClawWsSessionManager _sessionManager;

    public OpenClawNode(NodeExecutionManager executionManager, OpenClawWsSessionManager sessionManager)
        : base(executionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string NodeType => "OpenClaw";

    public override List<string> GetOutputParameters() => new() { "WsPath", "Status", "Connections" };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<OpenClawConfig>(node.Configuration ?? "{}") ?? new OpenClawConfig();

        // ── If this is a re-trigger (already running), forward input data if configured ──
        if (_executionManager.IsRunning(node.Id))
        {
            if (config.ForwardInput && config.InputMappings.Count > 0)
            {
                var forwarded = ResolveForwardedData(config, inputData);
                if (forwarded.Count > 0)
                {
                    await _sessionManager.BroadcastInputDataAsync(node.Id, forwarded);
                    Log(node, NodeLogLevel.Info,
                        $"Forwarded {forwarded.Count} field(s) to {_sessionManager.ConnectionCount(node.Id)} client(s)",
                        string.Join(", ", forwarded.Keys));
                }
            }

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "WsPath", $"/api/openclaw/ws/{node.Id}" },
                    { "Status", "running" },
                    { "Connections", _sessionManager.ConnectionCount(node.Id) }
                }
            };
        }

        // ── First run: arm the bridge ─────────────────────────────────────────
        node.Status = NodeStatus.Running;
        _executionManager.RegisterActiveExecution(node.Id, new OpenClawBridgeExecution());

        var wsPath = $"/api/openclaw/ws/{node.Id}";
        var secretHint = string.IsNullOrEmpty(config.TriggerSecret) ? "No auth (open)" : "Secret required";
        var forwardHint = config.ForwardInput && config.InputMappings.Count > 0
            ? $"Input forwarding: {config.InputMappings.Count} field(s)"
            : "Input forwarding: disabled";

        Log(node, NodeLogLevel.Info,
            "OpenClaw bridge armed",
            $"WebSocket path: {wsPath}\n" +
            $"Auth: {secretHint}\n" +
            $"{forwardHint}\n\n" +
            $"OpenClaw should connect to:\n  ws://<this-server>{wsPath}\n\n" +
            $"On connect, S2G returns the list of available nodes in this workflow.");

        return await Task.FromResult(new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "WsPath", wsPath },
                { "Status", "armed" },
                { "Connections", 0 }
            }
        });
    }

    private static Dictionary<string, object?> ResolveForwardedData(
        OpenClawConfig config, Dictionary<string, object?> inputData)
    {
        var result = new Dictionary<string, object?>();
        foreach (var mapping in config.InputMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Key) || string.IsNullOrWhiteSpace(mapping.SourceField))
                continue;

            // Try direct key match first, then strip {{...}} placeholder syntax
            var sourceKey = mapping.SourceField.Trim().Trim('{', '}').Trim();
            // Allow matching by full placeholder like {{NodeName.Field}} or just the raw key
            var value = inputData.TryGetValue(sourceKey, out var v)
                ? v
                : inputData.FirstOrDefault(kv =>
                    kv.Key.EndsWith("." + sourceKey, StringComparison.OrdinalIgnoreCase)).Value;

            result[mapping.Key] = value;
        }
        return result;
    }
}

/// <summary>Keeps the OpenClaw node in Running state until explicitly stopped.</summary>
file sealed class OpenClawBridgeExecution : IDisposable
{
    public void Dispose() { }
}

// ─── Configuration ─────────────────────────────────────────────────────────────

public class OpenClawConfig
{
    public string? TriggerSecret { get; set; }
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>When true, incoming inputData is resolved through InputMappings and
    /// broadcast to connected OpenClaw clients as a {"type":"data",...} frame.</summary>
    public bool ForwardInput { get; set; } = false;

    /// <summary>Key → source field mappings for input forwarding.</summary>
    public List<InputMapping> InputMappings { get; set; } = new();

    /// <summary>Saved template for the manual payload textarea.</summary>
    public string? StaticPayload { get; set; }
}

public class InputMapping
{
    /// <summary>The field name as seen by OpenClaw in the data frame.</summary>
    public string Key { get; set; } = "";

    /// <summary>The upstream input data key or placeholder (e.g. "TriggerNode.Body").</summary>
    public string SourceField { get; set; } = "";
}
