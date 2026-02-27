using Microsoft.AspNetCore.Identity;

namespace S2GPulseWeb.Web.Data;

public class ApplicationUser : IdentityUser
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    
    /// <summary>
    /// Timestamp when user accepted Terms of Service
    /// </summary>
    public DateTime? TermsAcceptedAt { get; set; }
    
    /// <summary>
    /// Version of Terms of Service the user accepted
    /// </summary>
    public int? TermsAcceptedVersion { get; set; }
    
    /// <summary>
    /// Timestamp when user accepted Privacy Statement
    /// </summary>
    public DateTime? PrivacyAcceptedAt { get; set; }
    
    /// <summary>
    /// Version of Privacy Statement the user accepted
    /// </summary>
    public int? PrivacyAcceptedVersion { get; set; }
    
    /// <summary>
    /// Check if user has accepted all required legal documents
    /// </summary>
    public bool HasAcceptedLegalTerms => TermsAcceptedAt.HasValue && PrivacyAcceptedAt.HasValue;
}
