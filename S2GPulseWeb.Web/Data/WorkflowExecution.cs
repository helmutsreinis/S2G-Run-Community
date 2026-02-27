using System;

namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Status of a workflow execution
/// </summary>
public enum WorkflowExecutionStatus
{
    Stopped,
    Running,
    Paused
}

/// <summary>
/// Tracks the execution state of a workflow
/// </summary>
public class WorkflowExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public WorkflowExecutionStatus Status { get; set; } = WorkflowExecutionStatus.Stopped;
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public int ExecutionCount { get; set; } = 0;
    public DateTime? LastExecutedAt { get; set; }
    
    // Navigation property
    public Workflow? Workflow { get; set; }
}
