namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Category for organizing custom nodes in the admin panel and designer toolbar.
/// </summary>
public class CustomNodeCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>Display name for the category (e.g., "Data Transformers")</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Optional description for the category</summary>
    public string? Description { get; set; }
    
    /// <summary>Emoji icon for visual identification (fallback if no SVG)</summary>
    public string IconEmoji { get; set; } = "🔧";
    
    /// <summary>Raw SVG markup for the category icon (preferred over emoji)</summary>
    public string? IconSvg { get; set; }
    
    /// <summary>Order for display in UI (lower = first)</summary>
    public int DisplayOrder { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Whether this category is visible in the designer</summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>Navigation property to custom nodes in this category</summary>
    public ICollection<CustomNodeDefinition> Nodes { get; set; } = new List<CustomNodeDefinition>();
}
