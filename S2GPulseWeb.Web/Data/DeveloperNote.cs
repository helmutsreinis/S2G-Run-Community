namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Represents a developer note/announcement displayed to users
/// </summary>
public class DeveloperNote
{
    public int Id { get; set; }
    
    /// <summary>
    /// Note title
    /// </summary>
    public string Title { get; set; } = "";
    
    /// <summary>
    /// HTML content of the note
    /// </summary>
    public string Content { get; set; } = "";
    
    /// <summary>
    /// When this note was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When this note was published (null = draft)
    /// </summary>
    public DateTime? PublishedAt { get; set; }
    
    /// <summary>
    /// Whether this note is visible to users
    /// </summary>
    public bool IsPublished { get; set; }
    
    /// <summary>
    /// Display order (lower = first)
    /// </summary>
    public int DisplayOrder { get; set; }
    
    /// <summary>
    /// If true, show this note as a one-time popup to users at login
    /// </summary>
    public bool ShowAsNewsletter { get; set; }
    
    /// <summary>
    /// Target page for newsletter popup (Home, Workflow, Logs, Settings, Connections, Admin, All)
    /// Default: Home. If "All", shows on any page.
    /// </summary>
    public string TargetPage { get; set; } = "Home";
}

