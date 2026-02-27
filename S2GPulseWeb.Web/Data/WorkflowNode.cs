namespace S2GPulseWeb.Web.Data;

public class WorkflowNode
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Workflow Workflow { get; set; } = null!;
    public string NodeType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Configuration { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Width { get; set; } = 200;
    public double Height { get; set; } = 100;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public NodeStatus Status { get; set; } = NodeStatus.Idle;
    public ICollection<NodeLogEntry> ActivityLogs { get; set; } = new List<NodeLogEntry>();
    public bool IsTrigger { get; set; } = false;
    public string? TagsJson { get; set; } // Stored as JSON array string
    public string? LoggingSettingsJson { get; set; } // Stored as JSON object (disabled by default)
    public string? IconOverride { get; set; } // Custom icon emoji/string (null = use default)
    public string? SurfaceFieldsJson { get; set; } // Stored as JSON array string
    
    public ICollection<WorkflowConnection> OutgoingConnections { get; set; } = new List<WorkflowConnection>();
    public ICollection<WorkflowConnection> IncomingConnections { get; set; } = new List<WorkflowConnection>();
}

public class WorkflowConnection
{
    public Guid Id { get; set; }
    public Guid SourceNodeId { get; set; }
    public WorkflowNode SourceNode { get; set; } = null!;
    public Guid TargetNodeId { get; set; }
    public WorkflowNode TargetNode { get; set; } = null!;
    public string? Label { get; set; }
}
