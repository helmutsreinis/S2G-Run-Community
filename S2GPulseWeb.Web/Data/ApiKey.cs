namespace S2GPulseWeb.Web.Data;

/// <summary>
/// API key entity for programmatic access to the Workflow API.
/// Keys are scoped to a single user and inherit their permissions.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>FK to ApplicationUser</summary>
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }
    
    /// <summary>Human-readable name (e.g. "CI/CD Key")</summary>
    public string Name { get; set; } = "";
    
    /// <summary>SHA-256 hash of the key (never store plain-text)</summary>
    public string KeyHash { get; set; } = "";
    
    /// <summary>First 8 chars for display (e.g. "pls_a1b2...")</summary>
    public string KeyPrefix { get; set; } = "";
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; } = false;
}
