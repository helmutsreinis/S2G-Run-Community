namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Defines the OAuth consent types for platform connectors.
/// Each type uses different Microsoft API endpoints and scopes.
/// </summary>
public enum ConnectorConsentType
{
    /// <summary>Microsoft Graph API (OneDrive, Mail, Calendar, etc.)</summary>
    Graph,
    
    /// <summary>Partner Center API for CSP operations</summary>
    PartnerCenter,
    
    /// <summary>Azure Management API for resource management</summary>
    AzureManagement,
    
    /// <summary>GitHub Copilot API via OAuth Device Flow</summary>
    GitHubCopilot
}
