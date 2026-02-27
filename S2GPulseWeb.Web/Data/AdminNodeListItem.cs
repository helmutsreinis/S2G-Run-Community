namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Lightweight DTO for admin node list display.
/// Contains only the minimal data needed to show nodes in the Admin Node Designer list.
/// Full definition is loaded on-demand when editing.
/// </summary>
public class AdminNodeListItem
{
    public Guid Id { get; set; }
    public string NodeTypeKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string IconSvg { get; set; } = "";
    public bool IsEnabled { get; set; }
    public CustomNodeExecutionType ExecutionType { get; set; }
    public int Version { get; set; }
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategoryEmoji { get; set; }
}
