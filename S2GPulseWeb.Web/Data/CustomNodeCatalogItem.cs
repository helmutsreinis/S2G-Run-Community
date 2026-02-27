namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Lightweight DTO for custom node catalog display.
/// Contains only the minimal data needed to show nodes in the Designer palette.
/// </summary>
public class CustomNodeCatalogItem
{
    public Guid Id { get; set; }
    public string NodeTypeKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string IconSvg { get; set; } = "";
    public string? IconFallbackEmoji { get; set; }
    public Guid? CategoryId { get; set; }
}
