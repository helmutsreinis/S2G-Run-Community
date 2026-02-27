using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Manages a single live WebSocket connection from OpenClaw.
/// Receives execute requests, runs target nodes via NodeExecutorFactory,
/// and streams results back over the WebSocket.
/// Also logs every frame to OpenClawWsSessionManager for the Live View feature.
/// </summary>
public class OpenClawWsSession
{
    private readonly WebSocket _ws;
    private readonly Guid _ocNodeId;
    private readonly string? _secret;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NodeExecutionManager _executionManager;
    private readonly OpenClawWsSessionManager _sessionManager;
    private readonly ILogger<OpenClawWsSession> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public OpenClawWsSession(
        WebSocket ws,
        Guid ocNodeId,
        string? secret,
        IServiceScopeFactory scopeFactory,
        NodeExecutionManager executionManager,
        OpenClawWsSessionManager sessionManager,
        ILogger<OpenClawWsSession> logger)
    {
        _ws = ws;
        _ocNodeId = ocNodeId;
        _secret = secret;
        _scopeFactory = scopeFactory;
        _executionManager = executionManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the WebSocket message loop until the client disconnects or the node stops.
    /// Returns only after the connection has fully closed.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var authenticated = string.IsNullOrEmpty(_secret);

        try
        {
            // ── Auth handshake ────────────────────────────────────────────────
            if (!authenticated)
            {
                var authFrame = await ReceiveJsonAsync(buffer, ct);
                if (authFrame == null)
                {
                    await SendAsync(new { type = "auth_failed", message = "Expected auth frame first" }, ct);
                    await _ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth required", ct);
                    return;
                }

                var af = authFrame.Value;
                _sessionManager.PushLog(_ocNodeId, "←", "auth", TrimPreview(af.GetRawText()));

                if (!af.TryGetProperty("type", out var t) || t.GetString() != "auth")
                {
                    await SendAsync(new { type = "auth_failed", message = "Expected auth frame first" }, ct);
                    await _ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth required", ct);
                    return;
                }

                var provided = af.TryGetProperty("secret", out var s) ? s.GetString() : null;
                if (provided != _secret)
                {
                    var deny = new { type = "auth_failed", message = "Invalid secret" };
                    await SendAsync(deny, ct);
                    _sessionManager.PushLog(_ocNodeId, "→", "auth_failed", "Invalid secret");
                    await _ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid secret", ct);
                    return;
                }

                authenticated = true;
            }

            // ── Send connected + available node list ──────────────────────────
            var availableNodes = await GetAvailableNodesAsync();
            var connectedMsg = new
            {
                type = "connected",
                nodeId = _ocNodeId,
                availableNodes
            };
            await SendAsync(connectedMsg, ct);
            _sessionManager.PushLog(_ocNodeId, "→", "connected",
                $"{availableNodes.Length} nodes available");

            _logger.LogInformation("[OpenClawWS] Client connected to node {NodeId}. {Count} available nodes.",
                _ocNodeId, availableNodes.Length);

            // ── Main message loop ─────────────────────────────────────────────
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var frameNullable = await ReceiveJsonAsync(buffer, ct);
                if (frameNullable == null) break;

                var frame = frameNullable.Value;
                var msgType = frame.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "unknown";

                // Log received frame (skip ping to avoid noise)
                if (msgType != "ping")
                    _sessionManager.PushLog(_ocNodeId, "←", msgType ?? "unknown", TrimPreview(frame.GetRawText()));

                switch (msgType)
                {
                    case "ping":
                        await SendAsync(new { type = "pong" }, ct);
                        break;

                    case "list_nodes":
                        var nodes = await GetAvailableNodesAsync();
                        await SendAsync(new { type = "node_list", nodes }, ct);
                        _sessionManager.PushLog(_ocNodeId, "→", "node_list", $"{nodes.Length} nodes");
                        break;

                    case "execute":
                        await HandleExecuteAsync(frame, ct);
                        break;

                    default:
                        _logger.LogWarning("[OpenClawWS] Unknown message type: {Type}", msgType);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("[OpenClawWS] WebSocket closed: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenClawWS] Unexpected error in session for node {NodeId}", _ocNodeId);
        }
        finally
        {
            _sessionManager.PushLog(_ocNodeId, "←", "disconnected", "Client disconnected");
            _logger.LogInformation("[OpenClawWS] Session ended for node {NodeId}", _ocNodeId);
        }
    }

    /// <summary>
    /// Sends a {"type":"data","data":{...}} frame to this client.
    /// Called by the session manager when upstream data is forwarded.
    /// </summary>
    public async Task SendDataFrameAsync(Dictionary<string, object?> data, CancellationToken ct = default)
    {
        if (_ws.State != WebSocketState.Open) return;
        var payload = new { type = "data", data };
        await SendAsync(payload, ct);
    }

    // ── Execute handler ───────────────────────────────────────────────────────

    private async Task HandleExecuteAsync(JsonElement frame, CancellationToken ct)
    {
        var requestId = frame.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "";
        var targetNodeIdStr = frame.TryGetProperty("nodeId", out var nid) ? nid.GetString() : null;
        var paramsEl = frame.TryGetProperty("params", out var p) ? (JsonElement?)p : null;

        if (!Guid.TryParse(targetNodeIdStr, out var targetNodeId))
        {
            await SendAsync(new { type = "error", requestId, message = "Invalid or missing nodeId" }, ct);
            _sessionManager.PushLog(_ocNodeId, "→", "error", "Invalid nodeId");
            return;
        }

        _logger.LogInformation("[OpenClawWS] Executing node {TargetNodeId} for request {RequestId}",
            targetNodeId, requestId);

        try
        {
            var inputData = new Dictionary<string, object?>();
            if (paramsEl.HasValue && paramsEl.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in paramsEl.Value.EnumerateObject())
                {
                    inputData[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
                }
            }

            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var executorFactory = scope.ServiceProvider.GetRequiredService<NodeExecutorFactory>();

            await using var db = await dbFactory.CreateDbContextAsync();
            var targetNode = await db.WorkflowNodes.AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == targetNodeId);

            if (targetNode == null)
            {
                await SendAsync(new { type = "error", requestId, message = $"Node {targetNodeId} not found" }, ct);
                _sessionManager.PushLog(_ocNodeId, "→", "error", $"Node {targetNodeId} not found");
                return;
            }

            var configJson = targetNode.Configuration ?? "{}";
            if (inputData.Count > 0)
            {
                var configDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson) ?? new();
                foreach (var (key, value) in inputData)
                    configDict[key] = JsonSerializer.SerializeToElement(value?.ToString() ?? "");
                configJson = JsonSerializer.Serialize(configDict);
            }

            var nodeToExecute = new WorkflowNode
            {
                Id = targetNode.Id,
                Name = targetNode.Name,
                NodeType = targetNode.NodeType,
                Configuration = configJson,
                WorkflowId = targetNode.WorkflowId,
                Status = NodeStatus.Idle
            };

            inputData["_WorkflowId"] = targetNode.WorkflowId.ToString();
            inputData["_NodeId"] = targetNode.Id.ToString();

            var executor = executorFactory.CreateExecutor(targetNode.NodeType);
            var result = await executor.ExecuteAsync(nodeToExecute, inputData, "openclaw-ws");

            if (result.Success)
            {
                await SendAsync(new { type = "result", requestId, success = true, output = result.OutputData }, ct);
                _sessionManager.PushLog(_ocNodeId, "→", "result",
                    $"req={requestId[..Math.Min(8, requestId.Length)]} success");
            }
            else
            {
                await SendAsync(new { type = "error", requestId, message = result.ErrorMessage ?? "Execution failed" }, ct);
                _sessionManager.PushLog(_ocNodeId, "→", "error",
                    $"req={requestId[..Math.Min(8, requestId.Length)]} {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenClawWS] Error executing node {TargetNodeId}", targetNodeId);
            await SendAsync(new { type = "error", requestId, message = ex.Message }, ct);
            _sessionManager.PushLog(_ocNodeId, "→", "error", ex.Message[..Math.Min(60, ex.Message.Length)]);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<AvailableNodeInfo[]> GetAvailableNodesAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var ocNode = await db.WorkflowNodes.AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == _ocNodeId);

            if (ocNode == null) return Array.Empty<AvailableNodeInfo>();

            var siblings = await db.WorkflowNodes.AsNoTracking()
                .Where(n => n.WorkflowId == ocNode.WorkflowId && n.Id != _ocNodeId)
                .ToListAsync();

            return siblings.Select(n => new AvailableNodeInfo
            {
                NodeId = n.Id,
                Name = n.Name,
                NodeType = n.NodeType,
                OutputParams = Components.Pages.Workflow.Designer.NodeHelper.GetOutputParametersForType(n.NodeType)
            }).ToArray();
        }
        catch
        {
            return Array.Empty<AvailableNodeInfo>();
        }
    }

    private async Task<JsonElement?> ReceiveJsonAsync(byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        WebSocketReceiveResult result;

        do
        {
            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        var raw = sb.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try { return JsonSerializer.Deserialize<JsonElement>(raw, _jsonOpts); }
        catch
        {
            _logger.LogWarning("[OpenClawWS] Received malformed JSON: {Raw}", raw[..Math.Min(200, raw.Length)]);
            return null;
        }
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(payload, _jsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);
    }

    private static string TrimPreview(string raw, int max = 80) =>
        raw.Length <= max ? raw : raw[..max] + "…";
}

// ── Node info ─────────────────────────────────────────────────────────────────

/// <summary>Describes a workflow node available to OpenClaw as a tool.</summary>
public class AvailableNodeInfo
{
    public Guid NodeId { get; set; }
    public string Name { get; set; } = "";
    public string NodeType { get; set; } = "";
    public List<string> OutputParams { get; set; } = new();
}
