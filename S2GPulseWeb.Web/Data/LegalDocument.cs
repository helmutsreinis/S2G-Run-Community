namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Represents a versioned legal document (Terms of Service or Privacy Policy)
/// </summary>
public class LegalDocument
{
    public int Id { get; set; }
    
    /// <summary>
    /// Type of legal document
    /// </summary>
    public LegalDocumentType Type { get; set; }
    
    /// <summary>
    /// Sequential version number for this document type
    /// </summary>
    public int Version { get; set; }
    
    /// <summary>
    /// Document title
    /// </summary>
    public string Title { get; set; } = "";
    
    /// <summary>
    /// HTML content of the document
    /// </summary>
    public string Content { get; set; } = "";
    
    /// <summary>
    /// When this version was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When this version was published (null = draft)
    /// </summary>
    public DateTime? PublishedAt { get; set; }
    
    /// <summary>
    /// Whether this is the current active version
    /// </summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// Types of legal documents
/// </summary>
public enum LegalDocumentType
{
    TermsOfService,
    PrivacyPolicy
}
