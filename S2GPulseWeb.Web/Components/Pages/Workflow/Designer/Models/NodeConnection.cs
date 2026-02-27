namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Represents which side of a node a connection port is on.
/// </summary>
public enum ConnectionSide
{
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>
/// Represents a connection between two nodes on the canvas.
/// </summary>
public class NodeConnection
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public ConnectionSide SourceSide { get; set; } = ConnectionSide.Right;
    public ConnectionSide TargetSide { get; set; } = ConnectionSide.Left;
    public string? Label { get; set; }
}
