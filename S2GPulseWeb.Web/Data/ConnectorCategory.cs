namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Category for grouping platform connectors in the admin panel and connections page.
/// </summary>
public class ConnectorCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>Display name for the category (e.g., "Partner Operations")</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Optional description for the category</summary>
    public string? Description { get; set; }
    
    /// <summary>Emoji icon for visual identification (e.g., "🏢")</summary>
    public string IconEmoji { get; set; } = "📁";
    
    /// <summary>Order for display in UI (lower = first)</summary>
    public int DisplayOrder { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Navigation property to connectors in this category</summary>
    public ICollection<PlatformConnector> Connectors { get; set; } = new List<PlatformConnector>();
}
