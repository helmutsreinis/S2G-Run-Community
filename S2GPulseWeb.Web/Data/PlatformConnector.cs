namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Admin-defined OAuth connector that users can consent to from the Connections page.
/// Stores Azure AD app credentials and configuration.
/// </summary>
public class PlatformConnector
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>Optional category for grouping (nullable for uncategorized)</summary>
    public Guid? CategoryId { get; set; }
    public ConnectorCategory? Category { get; set; }
    
    /// <summary>Display name for the connector (e.g., "Production Graph Access")</summary>
    public string Name { get; set; } = "";
    
    /// <summary>User-facing explanation text (supports HTML)</summary>
    public string Description { get; set; } = "";
    
    /// <summary>Type of OAuth consent (Graph, PartnerCenter, AzureManagement)</summary>
    public ConnectorConsentType ConsentType { get; set; } = ConnectorConsentType.Graph;
    
    /// <summary>Azure AD Application (client) ID</summary>
    public string ClientId { get; set; } = "";
    
    /// <summary>Client secret stored as plain text (admin-viewable)</summary>
    public string ClientSecret { get; set; } = "";
    
    /// <summary>Deprecated: Encrypted client secret (kept for migration only)</summary>
    public string ClientSecretEncrypted { get; set; } = "";
    
    /// <summary>Tenant ID or "common" for multi-tenant</summary>
    public string TenantId { get; set; } = "common";
    
    /// <summary>Comma-separated list of required OAuth scopes</summary>
    public string RequiredScopes { get; set; } = "";
    
    /// <summary>Whether this connector is available to users</summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>Order for display within category (lower = first)</summary>
    public int DisplayOrder { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Navigation property to user connections using this connector</summary>
    public ICollection<OAuthConnection> Connections { get; set; } = new List<OAuthConnection>();
}
