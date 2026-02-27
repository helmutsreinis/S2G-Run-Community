namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Represents a connection port position on a node.
/// </summary>
public struct PortPoint
{
    public double X;
    public double Y;
    public ConnectionSide Side;
}

/// <summary>
/// Represents the bounding rectangle of an element (for JS interop).
/// </summary>
public class BoundingClientRect
{
    public double Left { get; set; }
    public double Top { get; set; }
}
