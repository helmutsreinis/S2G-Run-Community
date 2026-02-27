namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Helper class for connection-related operations: port calculations, path generation, and routing.
/// </summary>
public static class ConnectionHelper
{
    /// <summary>
    /// Spacing between fanned-out connections in pixels.
    /// </summary>
    private const double FanSpacing = 16.0;

    /// <summary>
    /// Gets all connection port points for a node.
    /// </summary>
    public static List<PortPoint> GetNodePorts(CanvasNode node)
    {
        return new List<PortPoint>
        {
            new PortPoint { X = node.X + node.Width / 2, Y = node.Y, Side = ConnectionSide.Top },
            new PortPoint { X = node.X + node.Width / 2, Y = node.Y + node.Height, Side = ConnectionSide.Bottom },
            new PortPoint { X = node.X, Y = node.Y + node.Height / 2, Side = ConnectionSide.Left },
            new PortPoint { X = node.X + node.Width, Y = node.Y + node.Height / 2, Side = ConnectionSide.Right }
        };
    }

    /// <summary>
    /// Gets the closest ports between two nodes for optimal connection routing.
    /// </summary>
    public static (PortPoint Source, PortPoint Target) GetClosestPorts(CanvasNode source, CanvasNode target)
    {
        var srcP = GetNodePorts(source);
        var trgP = GetNodePorts(target);
        double minD = double.MaxValue;
        PortPoint bS = srcP[0], bT = trgP[0];
        
        foreach (var s in srcP)
        {
            foreach (var t in trgP)
            {
                var d = Math.Sqrt(Math.Pow(s.X - t.X, 2) + Math.Pow(s.Y - t.Y, 2));
                if (d < minD) { minD = d; bS = s; bT = t; }
            }
        }
        return (bS, bT);
    }

    /// <summary>
    /// Gets the closest ports between two nodes with fan offset for multiple connections.
    /// </summary>
    /// <param name="source">Source node</param>
    /// <param name="target">Target node</param>
    /// <param name="sourceIndex">Index of this connection among source's outgoing connections (0-based)</param>
    /// <param name="sourceTotal">Total outgoing connections from source to the same side</param>
    /// <param name="targetIndex">Index of this connection among target's incoming connections (0-based)</param>
    /// <param name="targetTotal">Total incoming connections to target from the same side</param>
    public static (PortPoint Source, PortPoint Target) GetClosestPortsWithFan(
        CanvasNode source, CanvasNode target,
        int sourceIndex = 0, int sourceTotal = 1,
        int targetIndex = 0, int targetTotal = 1)
    {
        var (baseSource, baseTarget) = GetClosestPorts(source, target);
        
        // Calculate fan offsets
        var sourceOffset = CalculateFanOffset(sourceIndex, sourceTotal);
        var targetOffset = CalculateFanOffset(targetIndex, targetTotal);
        
        // Apply offset based on port side
        var fanSource = ApplyFanOffset(baseSource, sourceOffset);
        var fanTarget = ApplyFanOffset(baseTarget, targetOffset);
        
        return (fanSource, fanTarget);
    }

    /// <summary>
    /// Calculates the offset for a given index in a fan arrangement.
    /// Only applies offset when there are multiple connections (total > 1).
    /// </summary>
    private static double CalculateFanOffset(int index, int total)
    {
        // Only fan out when there are multiple connections; single connections stay centered
        if (total <= 1) return 0;
        // Center the fan: for total=2, offsets are -8, +8; for total=3, offsets are -16, 0, +16
        return (index - (total - 1) / 2.0) * FanSpacing;
    }

    /// <summary>
    /// Applies a perpendicular fan offset to a port point based on its side.
    /// </summary>
    private static PortPoint ApplyFanOffset(PortPoint port, double offset)
    {
        return port.Side switch
        {
            // For left/right ports, offset vertically (Y)
            ConnectionSide.Left or ConnectionSide.Right => new PortPoint 
            { 
                X = port.X, 
                Y = port.Y + offset, 
                Side = port.Side 
            },
            // For top/bottom ports, offset horizontally (X)
            ConnectionSide.Top or ConnectionSide.Bottom => new PortPoint 
            { 
                X = port.X + offset, 
                Y = port.Y, 
                Side = port.Side 
            },
            _ => port
        };
    }

    /// <summary>
    /// Gets the port position for a specific side of a node.
    /// </summary>
    public static PortPoint GetPortPosition(CanvasNode node, ConnectionSide side)
    {
        return side switch
        {
            ConnectionSide.Top => new PortPoint { X = node.X + node.Width / 2, Y = node.Y, Side = ConnectionSide.Top },
            ConnectionSide.Bottom => new PortPoint { X = node.X + node.Width / 2, Y = node.Y + node.Height, Side = ConnectionSide.Bottom },
            ConnectionSide.Left => new PortPoint { X = node.X, Y = node.Y + node.Height / 2, Side = ConnectionSide.Left },
            ConnectionSide.Right => new PortPoint { X = node.X + node.Width, Y = node.Y + node.Height / 2, Side = ConnectionSide.Right },
            _ => new PortPoint { X = node.X + node.Width, Y = node.Y + node.Height / 2, Side = ConnectionSide.Right }
        };
    }

    /// <summary>
    /// Gets the port position for a specific side of a node with fan offset.
    /// </summary>
    public static PortPoint GetPortPositionWithFan(CanvasNode node, ConnectionSide side, int index = 0, int total = 1)
    {
        var basePort = GetPortPosition(node, side);
        var offset = CalculateFanOffset(index, total);
        return ApplyFanOffset(basePort, offset);
    }

    /// <summary>
    /// Generates a smart bezier path between two port points.
    /// </summary>
    public static string GenerateSmartBezierPath(PortPoint source, PortPoint target)
    {
        double dx = target.X - source.X;
        double dy = target.Y - source.Y;
        double controlOffset = Math.Min(Math.Abs(dx) / 2, 80);
        
        // Calculate control points based on connection sides
        double sx1 = source.X, sy1 = source.Y, sx2 = target.X, sy2 = target.Y;
        
        switch (source.Side)
        {
            case ConnectionSide.Right: sx1 = source.X + controlOffset; break;
            case ConnectionSide.Left: sx1 = source.X - controlOffset; break;
            case ConnectionSide.Bottom: sy1 = source.Y + controlOffset; break;
            case ConnectionSide.Top: sy1 = source.Y - controlOffset; break;
        }
        
        switch (target.Side)
        {
            case ConnectionSide.Left: sx2 = target.X - controlOffset; break;
            case ConnectionSide.Right: sx2 = target.X + controlOffset; break;
            case ConnectionSide.Top: sy2 = target.Y - controlOffset; break;
            case ConnectionSide.Bottom: sy2 = target.Y + controlOffset; break;
        }
        
        return $"M {source.X} {source.Y} C {sx1} {sy1}, {sx2} {sy2}, {target.X} {target.Y}";
    }
}
