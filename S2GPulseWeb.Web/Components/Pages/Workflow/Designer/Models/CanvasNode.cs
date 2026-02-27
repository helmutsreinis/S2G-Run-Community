namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

using S2GPulseWeb.Web.Data;

/// <summary>
/// Represents a node on the designer canvas with position, configuration, and runtime state.
/// </summary>
public class CanvasNode
{
    public Guid Id { get; set; }
    public string NodeType { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 60;
    public double Height { get; set; } = 60;
    public string? Configuration { get; set; }
    public NodeStatus Status { get; set; } = NodeStatus.Idle;
    public List<NodeLogEntry> ActivityLogs { get; set; } = new();
    public int StatusCode { get; set; }
    public Dictionary<string, object?> OutputData { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public bool IsTrigger { get; set; } = false;
    public NodeLoggingSettings LoggingSettings { get; set; } = new();
    public string? IconOverride { get; set; }
    
    /// <summary>
    /// Placeholder keys to display on the node surface (e.g., "SQLNode.rowsCount")
    /// </summary>
    public List<string> SurfaceFields { get; set; } = new();
}
