using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Singleton service that tracks active OpenClaw WebSocket sessions
/// and maintains a per-node activity log for the Live View feature.
/// </summary>
public class OpenClawWsSessionManager
{
    // nodeId → list of active sessions
    private readonly ConcurrentDictionary<Guid, List<OpenClawWsSession>> _sessions = new();
    // nodeId → ring buffer of recent log entries (capped at MaxLogEntries)
    private readonly ConcurrentDictionary<Guid, Queue<OpenClawWsLogEntry>> _logs = new();
    private readonly Lock _lock = new();

    private const int MaxLogEntries = 100;

    // ── Session management ────────────────────────────────────────────────────

    public void AddSession(Guid nodeId, OpenClawWsSession session)
    {
        _sessions.AddOrUpdate(
            nodeId,
            _ => new List<OpenClawWsSession> { session },
            (_, existing) =>
            {
                lock (_lock) { existing.Add(session); }
                return existing;
            });
    }

    public void RemoveSession(Guid nodeId, OpenClawWsSession session)
    {
        if (_sessions.TryGetValue(nodeId, out var list))
        {
            lock (_lock)
            {
                list.Remove(session);
                if (list.Count == 0)
                    _sessions.TryRemove(nodeId, out _);
            }
        }
    }

    public bool HasActiveSession(Guid nodeId) =>
        _sessions.TryGetValue(nodeId, out var list) && list.Count > 0;

    public int ConnectionCount(Guid nodeId) =>
        _sessions.TryGetValue(nodeId, out var list) ? list.Count : 0;

    // ── Activity log (Live View) ──────────────────────────────────────────────

    /// <summary>Push a log entry for the Live View ring buffer.</summary>
    public void PushLog(Guid nodeId, string direction, string messageType, string preview)
    {
        var entry = new OpenClawWsLogEntry(DateTime.UtcNow, direction, messageType, preview);
        var queue = _logs.GetOrAdd(nodeId, _ => new Queue<OpenClawWsLogEntry>());
        lock (queue)
        {
            queue.Enqueue(entry);
            while (queue.Count > MaxLogEntries)
                queue.Dequeue();
        }
    }

    /// <summary>Returns a snapshot of recent log entries for the given node.</summary>
    public List<OpenClawWsLogEntry> GetRecentLogs(Guid nodeId)
    {
        if (!_logs.TryGetValue(nodeId, out var queue)) return new();
        lock (queue) return queue.ToList();
    }

    /// <summary>Clears the log buffer for a node.</summary>
    public void ClearLogs(Guid nodeId)
    {
        if (_logs.TryGetValue(nodeId, out var queue))
            lock (queue) queue.Clear();
    }

    // ── Input forwarding (broadcast data frame to all sessions) ───────────────

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// Broadcasts a {"type":"data","data":{...}} frame to all active sessions for the node.
    /// Called by OpenClawNode when it receives upstream input data.
    /// </summary>
    public async Task BroadcastInputDataAsync(Guid nodeId, Dictionary<string, object?> data, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(nodeId, out var list)) return;

        List<OpenClawWsSession> snapshot;
        lock (_lock) { snapshot = list.ToList(); }

        var tasks = snapshot.Select(s => s.SendDataFrameAsync(data, ct));
        await Task.WhenAll(tasks);

        PushLog(nodeId, "→", "data", $"{data.Count} field(s) forwarded to {snapshot.Count} client(s)");
    }
}

// ── Log entry model ───────────────────────────────────────────────────────────

public sealed class OpenClawWsLogEntry
{
    public DateTime Timestamp { get; }
    public string Direction { get; }   // "→" = sent to OC, "←" = received from OC
    public string MessageType { get; }
    public string Preview { get; }

    public OpenClawWsLogEntry(DateTime timestamp, string direction, string messageType, string preview)
    {
        Timestamp = timestamp;
        Direction = direction;
        MessageType = messageType;
        Preview = preview;
    }

    /// <summary>Local-time display string for the UI.</summary>
    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string TypeColor => MessageType switch
    {
        "connected" => "#4ade80",
        "execute"   => "#60a5fa",
        "result"    => "#34d399",
        "error"     => "#f87171",
        "ping"      => "#94a3b8",
        "pong"      => "#94a3b8",
        "data"      => "#a78bfa",
        "auth"      => "#fbbf24",
        "auth_failed" => "#f87171",
        _           => "#e2e8f0",
    };
}
