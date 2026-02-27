namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Tracks which developer notes a user has dismissed (for newsletter popups)
/// </summary>
public class UserDismissedNote
{
    public int Id { get; set; }
    
    /// <summary>
    /// User who dismissed the note
    /// </summary>
    public string UserId { get; set; } = "";
    
    /// <summary>
    /// The note that was dismissed
    /// </summary>
    public int NoteId { get; set; }
    
    /// <summary>
    /// When the note was dismissed
    /// </summary>
    public DateTime DismissedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    public ApplicationUser? User { get; set; }
    public DeveloperNote? Note { get; set; }
}
