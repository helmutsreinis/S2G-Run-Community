using System;
using System.Collections.Generic;

namespace S2GPulseWeb.Web.Data;

public enum NodeStatus
{
    Idle,
    Running,
    Success,
    Failure
}

public enum NodeLogLevel
{
    Info,
    Warning,
    Error,
    Debug
}

public class NodeLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NodeId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public NodeLogLevel Level { get; set; } = NodeLogLevel.Info;
    public string Message { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public class NodeExecutionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object?> OutputData { get; set; } = new();
}
