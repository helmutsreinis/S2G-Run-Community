using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for handling OAuth2 authentication with Microsoft 365 and other providers.
/// Manages token storage, refresh, and provides authenticated clients.
/// </summary>
public class OAuthService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OAuthService> _logger;

    // Microsoft OAuth endpoints
    private const string MicrosoftAuthorizeUrl = "https://login.microsoftonline.com/{0}/oauth2/v2.0/authorize";
    private const string MicrosoftTokenUrl = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";
    
    // Default scopes for OneDrive access
    private static readonly string[] DefaultScopes = new[]
    {
        "offline_access",  // Required for refresh tokens
        "Files.Read.All",
        "Files.ReadWrite.All",
        "User.Read"
    };

    public OAuthService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OAuthService> logger)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets the Microsoft 365 OAuth settings from user secrets.
    /// </summary>
    public async Task<M365Settings?> GetM365SettingsAsync(string userId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        
        var clientId = await db.UserSecrets
            .Where(s => s.UserId == userId && s.Name == "M365_ClientId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
            
        var clientSecret = await db.UserSecrets
            .Where(s => s.UserId == userId && s.Name == "M365_ClientSecret")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
            
        var tenantId = await db.UserSecrets
            .Where(s => s.UserId == userId && s.Name == "M365_TenantId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return null;

        return new M365Settings
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            TenantId = tenantId ?? "common"  // "common" for multi-tenant
        };
    }

    /// <summary>
    /// Generates the Microsoft OAuth2 authorization URL.
    /// </summary>
    public async Task<string?> GetAuthorizationUrlAsync(string userId, string redirectUri, string state)
    {
        var settings = await GetM365SettingsAsync(userId);
        if (settings == null)
            return null;

        var scopes = string.Join(" ", DefaultScopes);
        var authorizeUrl = string.Format(MicrosoftAuthorizeUrl, settings.TenantId);
        
        var url = $"{authorizeUrl}?" +
            $"client_id={Uri.EscapeDataString(settings.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&response_mode=query";
            
        return url;
    }

    /// <summary>
    /// Exchanges an authorization code for access and refresh tokens.
    /// </summary>
    public async Task<OAuthConnection?> ExchangeCodeForTokensAsync(
        string userId, 
        string code, 
        string redirectUri, 
        string connectionName)
    {
        var settings = await GetM365SettingsAsync(userId);
        if (settings == null)
        {
            _logger.LogError("M365 settings not found for user {UserId}", userId);
            return null;
        }

        var tokenUrl = string.Format(MicrosoftTokenUrl, settings.TenantId);
        var client = _httpClientFactory.CreateClient();

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = string.Join(" ", DefaultScopes)
        });

        try
        {
            var response = await client.PostAsync(tokenUrl, content);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token exchange failed: {StatusCode} - {Response}", response.StatusCode, json);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            if (tokenResponse == null)
                return null;

            // Get user info
            var email = await GetUserEmailAsync(tokenResponse.access_token);

            // Create and save connection
            var connection = new OAuthConnection
            {
                UserId = userId,
                ConnectionName = connectionName,
                Provider = "Microsoft365",
                AccessToken = tokenResponse.access_token,
                RefreshToken = tokenResponse.refresh_token ?? "",
                TokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.expires_in - 60), // 60s buffer
                Scopes = string.Join(" ", DefaultScopes),
                TenantId = settings.TenantId,
                Email = email
            };

            using var db = await _dbFactory.CreateDbContextAsync();
            db.OAuthConnections.Add(connection);
            await db.SaveChangesAsync();

            _logger.LogInformation("Created OAuth connection for user {UserId}: {ConnectionName}", userId, connectionName);
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exchanging authorization code for tokens");
            return null;
        }
    }

    /// <summary>
    /// Refreshes an expired access token using the refresh token.
    /// Uses platform connector credentials if the connection was created via a connector.
    /// </summary>
    public async Task<bool> RefreshTokenAsync(Guid connectionId, PlatformConnectorService? connectorService = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connection = await db.OAuthConnections
            .Include(c => c.PlatformConnector)
            .FirstOrDefaultAsync(c => c.Id == connectionId);
        
        if (connection == null || string.IsNullOrEmpty(connection.RefreshToken))
            return false;

        string clientId;
        string clientSecret;
        string tenantId = connection.TenantId ?? "common";

        // Check if connection was created via a platform connector
        if (connection.PlatformConnectorId.HasValue && connection.PlatformConnector != null)
        {
            // Use platform connector credentials
            clientId = connection.PlatformConnector.ClientId;
            
            // Get secret - prefer plain text, fallback to legacy
            clientSecret = !string.IsNullOrEmpty(connection.PlatformConnector.ClientSecret)
                ? connection.PlatformConnector.ClientSecret
                : connectorService != null
                    ? await connectorService.GetClientSecretAsync(connection.PlatformConnectorId.Value) ?? ""
                    : "";
            
            if (string.IsNullOrEmpty(clientSecret))
            {
                _logger.LogError("Could not retrieve client secret for platform connector {ConnectorId}", connection.PlatformConnectorId);
                return false;
            }
            
            _logger.LogDebug("Refreshing token using platform connector {ConnectorId}", connection.PlatformConnectorId);
        }
        else
        {
            // Fall back to user secrets (legacy flow)
            var settings = await GetM365SettingsAsync(connection.UserId);
            if (settings == null)
            {
                _logger.LogError("M365 settings not found for user {UserId} and connection has no platform connector", connection.UserId);
                return false;
            }
            
            clientId = settings.ClientId;
            clientSecret = settings.ClientSecret;
        }

        var tokenUrl = string.Format(MicrosoftTokenUrl, tenantId);
        var client = _httpClientFactory.CreateClient();

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = connection.RefreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = connection.Scopes
        });

        try
        {
            var response = await client.PostAsync(tokenUrl, content);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token refresh failed for connection {ConnectionId}: {Response}", connectionId, json);
                return false;
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            if (tokenResponse == null)
                return false;

            connection.AccessToken = tokenResponse.access_token;
            connection.RefreshToken = tokenResponse.refresh_token ?? connection.RefreshToken;
            connection.TokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.expires_in - 60);

            await db.SaveChangesAsync();
            _logger.LogInformation("Refreshed token for connection {ConnectionId}", connectionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token for connection {ConnectionId}", connectionId);
            return false;
        }
    }

    /// <summary>
    /// Gets a valid access token for a connection, refreshing if needed.
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync(Guid connectionId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var connection = await db.OAuthConnections.FindAsync(connectionId);
        
        if (connection == null)
            return null;

        // Refresh if expired or expiring soon
        if (connection.TokenExpiry <= DateTime.UtcNow.AddMinutes(5))
        {
            if (!await RefreshTokenAsync(connectionId))
                return null;
            
            // Reload after refresh
            connection = await db.OAuthConnections.FindAsync(connectionId);
        }

        // Update last used
        if (connection != null)
        {
            connection.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return connection?.AccessToken;
    }

    /// <summary>
    /// Creates an authenticated GraphServiceClient for a connection.
    /// </summary>
    public async Task<GraphServiceClient?> GetGraphClientAsync(Guid connectionId)
    {
        var accessToken = await GetValidAccessTokenAsync(connectionId);
        if (string.IsNullOrEmpty(accessToken))
            return null;

        // Use the new Graph SDK v5 authentication pattern
        var tokenProvider = new StaticAccessTokenProvider(accessToken);
        var authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
        
        return new GraphServiceClient(authProvider);
    }

    /// <summary>
    /// Gets all connections for a user in personal or organization context.
    /// </summary>
    public async Task<List<OAuthConnection>> GetConnectionsAsync(string userId, Guid? organizationId = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        
        if (organizationId.HasValue)
        {
            return await db.OAuthConnections
                .Where(c => c.OrganizationId == organizationId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
        
        return await db.OAuthConnections
            .Where(c => c.UserId == userId && c.OrganizationId == null)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }
    
    /// <summary>
    /// Gets a specific connection in personal or organization context.
    /// No fallback between contexts - strict isolation.
    /// </summary>
    public async Task<OAuthConnection?> GetConnectionByProviderAsync(string userId, string provider, Guid? organizationId = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        
        if (organizationId.HasValue)
        {
            // Organization context - only org connections
            return await db.OAuthConnections
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Provider == provider);
        }
        
        // Personal context - only personal connections
        return await db.OAuthConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.OrganizationId == null && c.Provider == provider);
    }

    /// <summary>
    /// Deletes a connection.
    /// </summary>
    public async Task<bool> DeleteConnectionAsync(Guid connectionId, string userId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var connection = await db.OAuthConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId);
        
        if (connection == null)
            return false;

        db.OAuthConnections.Remove(connection);
        await db.SaveChangesAsync();
        return true;
    }

    #region Platform Connector OAuth

    /// <summary>
    /// Generates OAuth authorization URL using a platform connector's credentials.
    /// </summary>
    public async Task<string?> GetAuthorizationUrlForConnectorAsync(
        Guid connectorId,
        string redirectUri,
        string state,
        PlatformConnectorService connectorService)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connector = await db.PlatformConnectors.FindAsync(connectorId);
        if (connector == null || !connector.IsEnabled)
            return null;

        var clientSecret = await connectorService.GetClientSecretAsync(connectorId);
        if (string.IsNullOrEmpty(clientSecret))
            return null;

        var scopes = string.IsNullOrEmpty(connector.RequiredScopes)
            ? string.Join(" ", DefaultScopes)
            : connector.RequiredScopes;

        var authorizeUrl = string.Format(MicrosoftAuthorizeUrl, connector.TenantId);

        return $"{authorizeUrl}?" +
            $"client_id={Uri.EscapeDataString(connector.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&response_mode=query";
    }

    /// <summary>
    /// Exchanges authorization code for tokens using platform connector credentials.
    /// </summary>
    public async Task<OAuthConnection?> ExchangeCodeForConnectorAsync(
        Guid connectorId,
        string userId,
        string code,
        string redirectUri,
        string connectionName,
        PlatformConnectorService connectorService,
        Guid? organizationId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connector = await db.PlatformConnectors.FindAsync(connectorId);
        if (connector == null)
        {
            _logger.LogError("Platform connector {ConnectorId} not found", connectorId);
            return null;
        }

        var clientSecret = await connectorService.GetClientSecretAsync(connectorId);
        if (string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogError("Could not decrypt secret for connector {ConnectorId}", connectorId);
            return null;
        }

        var scopes = string.IsNullOrEmpty(connector.RequiredScopes)
            ? string.Join(" ", DefaultScopes)
            : connector.RequiredScopes;

        var tokenUrl = string.Format(MicrosoftTokenUrl, connector.TenantId);
        var client = _httpClientFactory.CreateClient();

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = connector.ClientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = scopes
        });

        try
        {
            var response = await client.PostAsync(tokenUrl, content);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token exchange failed for connector: {StatusCode} - {Response}", response.StatusCode, json);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            if (tokenResponse == null)
                return null;

            var email = await GetUserEmailAsync(tokenResponse.access_token);

            var connection = new OAuthConnection
            {
                UserId = userId,
                ConnectionName = connectionName,
                Provider = connector.ConsentType.ToString(),
                AccessToken = tokenResponse.access_token,
                RefreshToken = tokenResponse.refresh_token ?? "",
                TokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.expires_in - 60),
                Scopes = scopes,
                TenantId = connector.TenantId,
                Email = email,
                PlatformConnectorId = connectorId,
                OrganizationId = organizationId
            };

            db.OAuthConnections.Add(connection);
            await db.SaveChangesAsync();

            _logger.LogInformation("Created OAuth connection via platform connector for user {UserId}: {ConnectionName}", userId, connectionName);
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exchanging code for connector {ConnectorId}", connectorId);
            return null;
        }
    }

    #endregion

    private async Task<string?> GetUserEmailAsync(string accessToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            
            var response = await client.GetAsync("https://graph.microsoft.com/v1.0/me");
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("mail", out var mail) 
                ? mail.GetString() 
                : doc.RootElement.TryGetProperty("userPrincipalName", out var upn) 
                    ? upn.GetString() 
                    : null;
        }
        catch
        {
            return null;
        }
    }

    // Helper classes
    private class TokenResponse
    {
        public string access_token { get; set; } = "";
        public string? refresh_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; } = "";
    }
}

public class M365Settings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TenantId { get; set; } = "common";
}

/// <summary>
/// Static access token provider for Graph SDK v5.
/// </summary>
public class StaticAccessTokenProvider : IAccessTokenProvider
{
    private readonly string _accessToken;

    public StaticAccessTokenProvider(string accessToken)
    {
        _accessToken = accessToken;
    }

    public AllowedHostsValidator AllowedHostsValidator => new();

    public Task<string> GetAuthorizationTokenAsync(
        Uri uri, 
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_accessToken);
    }
}
