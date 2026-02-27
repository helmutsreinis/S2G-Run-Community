using System;

namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Database entity for persisted node execution logs
/// </summary>
public class NodeLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>Owner of the workflow that generated this log</summary>
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>Optional reference to the workflow</summary>
    public Guid? WorkflowId { get; set; }
    
    /// <summary>The node that generated this log</summary>
    public Guid NodeId { get; set; }
    
    /// <summary>Node name at time of execution</summary>
    public string NodeName { get; set; } = string.Empty;
    
    /// <summary>Node type (e.g., OpenAI, HttpRequest)</summary>
    public string NodeType { get; set; } = string.Empty;
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public NodeLogLevel Level { get; set; } = NodeLogLevel.Info;
    
    public string Message { get; set; } = string.Empty;
    
    /// <summary>Optional JSON detail data</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// User-specific log retention settings
/// </summary>
public class LogRetentionSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>Retention period value</summary>
    public int RetentionValue { get; set; } = 7;
    
    /// <summary>Retention period unit</summary>
    public RetentionUnit RetentionUnit { get; set; } = RetentionUnit.Days;
    
    // Navigation
    public ApplicationUser? User { get; set; }
}

public enum RetentionUnit
{
    Minutes,
    Hours,
    Days
}
