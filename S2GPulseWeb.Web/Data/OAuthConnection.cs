namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Represents an OAuth2 connection to an external service (e.g., Microsoft 365).
/// Tokens are stored per user and can be used by workflow nodes.
/// </summary>
public class OAuthConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>User who owns this connection</summary>
    public string UserId { get; set; } = "";
    
    /// <summary>Display name for this connection (e.g., "My OneDrive")</summary>
    public string ConnectionName { get; set; } = "";
    
    /// <summary>Provider identifier (e.g., "Microsoft365", "Google")</summary>
    public string Provider { get; set; } = "";
    
    /// <summary>OAuth2 access token (short-lived)</summary>
    public string AccessToken { get; set; } = "";
    
    /// <summary>OAuth2 refresh token (long-lived, used to get new access tokens)</summary>
    public string RefreshToken { get; set; } = "";
    
    /// <summary>When the access token expires</summary>
    public DateTime TokenExpiry { get; set; }
    
    /// <summary>OAuth scopes granted</summary>
    public string Scopes { get; set; } = "";
    
    /// <summary>Azure AD tenant ID (for Microsoft 365)</summary>
    public string? TenantId { get; set; }
    
    /// <summary>User's email from the provider</summary>
    public string? Email { get; set; }
    
    /// <summary>Optional link to admin-defined platform connector</summary>
    public Guid? PlatformConnectorId { get; set; }
    public PlatformConnector? PlatformConnector { get; set; }
    
    /// <summary>
    /// Organization ownership (null = personal connection).
    /// When set, connection is available to all organization workflows.
    /// </summary>
    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

