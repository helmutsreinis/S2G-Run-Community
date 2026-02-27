using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing GitHub Copilot connections and API access.
/// Handles OAuth device flow authentication and Copilot API token management.
/// </summary>
public class CopilotConnectorService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CopilotConnectorService> _logger;

    // GitHub OAuth endpoints
    private const string GitHubDeviceCodeUrl = "https://github.com/login/device/code";
    private const string GitHubTokenUrl = "https://github.com/login/oauth/access_token";
    
    // GitHub Copilot token endpoint (used to exchange GitHub token for Copilot API token)
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    
    // OpenAI-compatible Copilot chat endpoint
    private const string CopilotChatUrl = "https://api.githubcopilot.com/chat/completions";
    private const string CopilotModelsUrl = "https://api.githubcopilot.com/models";
    
    // VS Code Copilot extension's OAuth client ID (public, used by official extensions)
    private const string GitHubCopilotClientId = "Iv1.b507a08c87ecfe98";

    public CopilotConnectorService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<CopilotConnectorService> logger)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    #region Device Flow Authentication

    /// <summary>
    /// Initiates GitHub OAuth device flow for Copilot authentication.
    /// Returns device code, user code, and verification URL.
    /// </summary>
    public async Task<GitHubDeviceCodeResponse?> StartDeviceFlowAsync()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = GitHubCopilotClientId,
            ["scope"] = "read:user"
        });

        try
        {
            var response = await client.PostAsync(GitHubDeviceCodeUrl, content);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("GitHub device flow failed: {StatusCode} - {Response}", 
                    response.StatusCode, json);
                return null;
            }

            var result = JsonSerializer.Deserialize<GitHubDeviceCodeResponse>(json);
            _logger.LogInformation("GitHub device flow started. User code: {UserCode}", result?.UserCode);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting GitHub device flow");
            return null;
        }
    }

    /// <summary>
    /// Polls GitHub for access token after user authorizes via device code.
    /// Call this repeatedly until it returns a token or error.
    /// </summary>
    public async Task<GitHubTokenPollResult> PollForTokenAsync(string deviceCode)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = GitHubCopilotClientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
        });

        try
        {
            var response = await client.PostAsync(GitHubTokenUrl, content);
            var json = await response.Content.ReadAsStringAsync();
            
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for error states
            if (root.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                return error switch
                {
                    "authorization_pending" => new GitHubTokenPollResult { Status = TokenPollStatus.Pending },
                    "slow_down" => new GitHubTokenPollResult { Status = TokenPollStatus.SlowDown },
                    "expired_token" => new GitHubTokenPollResult { Status = TokenPollStatus.Expired, Error = "Device code expired" },
                    "access_denied" => new GitHubTokenPollResult { Status = TokenPollStatus.Denied, Error = "Access denied" },
                    _ => new GitHubTokenPollResult { Status = TokenPollStatus.Error, Error = error }
                };
            }

            // Success - we got an access token
            if (root.TryGetProperty("access_token", out var tokenProp))
            {
                return new GitHubTokenPollResult
                {
                    Status = TokenPollStatus.Success,
                    AccessToken = tokenProp.GetString()
                };
            }

            return new GitHubTokenPollResult { Status = TokenPollStatus.Error, Error = "Unknown response" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling for GitHub token");
            return new GitHubTokenPollResult { Status = TokenPollStatus.Error, Error = ex.Message };
        }
    }

    /// <summary>
    /// Exchanges a GitHub access token for a short-lived Copilot API token.
    /// The Copilot token is typically valid for ~10 minutes.
    /// </summary>
    public async Task<CopilotTokenResponse?> GetCopilotTokenAsync(string githubToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", githubToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("S2GRun/1.0");

        try
        {
            var response = await client.GetAsync(CopilotTokenUrl);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get Copilot token: {StatusCode} - {Response}",
                    response.StatusCode, json);
                return null;
            }

            return JsonSerializer.Deserialize<CopilotTokenResponse>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Copilot token");
            return null;
        }
    }

    #endregion

    #region Connection Management

    /// <summary>
    /// Creates an OAuthConnection after successful device flow.
    /// Stores the GitHub token as RefreshToken and Copilot token as AccessToken.
    /// </summary>
    public async Task<OAuthConnection?> CreateConnectionAsync(
        string userId,
        string connectionName,
        string githubToken,
        CopilotTokenResponse copilotToken,
        Guid? platformConnectorId = null)
    {
        try
        {
            // Get user info from GitHub
            var userInfo = await GetGitHubUserAsync(githubToken);
            
            var connection = new OAuthConnection
            {
                UserId = userId,
                ConnectionName = connectionName,
                Provider = "GitHubCopilot",
                AccessToken = copilotToken.Token,
                RefreshToken = githubToken,  // Store GitHub token for refreshing Copilot token
                TokenExpiry = DateTime.UtcNow.AddSeconds(copilotToken.ExpiresIn - 60),
                Scopes = "copilot",
                Email = userInfo?.Login ?? userInfo?.Email,
                PlatformConnectorId = platformConnectorId
            };

            await using var db = await _dbFactory.CreateDbContextAsync();
            db.OAuthConnections.Add(connection);
            await db.SaveChangesAsync();

            _logger.LogInformation("Created Copilot connection for user {UserId}: {ConnectionName}",
                userId, connectionName);
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Copilot connection");
            return null;
        }
    }

    /// <summary>
    /// Gets a valid Copilot API token for the connection, refreshing if needed.
    /// </summary>
    public async Task<string?> GetValidCopilotTokenAsync(Guid connectionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connection = await db.OAuthConnections.FindAsync(connectionId);

        if (connection == null || connection.Provider != "GitHubCopilot")
            return null;

        // Check if token is expired or expiring soon
        if (connection.TokenExpiry <= DateTime.UtcNow.AddMinutes(1))
        {
            // Refresh using stored GitHub token
            var newToken = await GetCopilotTokenAsync(connection.RefreshToken);
            if (newToken == null)
            {
                _logger.LogError("Failed to refresh Copilot token for connection {ConnectionId}", connectionId);
                return null;
            }

            connection.AccessToken = newToken.Token;
            connection.TokenExpiry = DateTime.UtcNow.AddSeconds(newToken.ExpiresIn - 60);
            await db.SaveChangesAsync();

            _logger.LogInformation("Refreshed Copilot token for connection {ConnectionId}", connectionId);
        }

        connection.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return connection.AccessToken;
    }

    /// <summary>
    /// Gets Copilot connections for a user or organization context.
    /// When organizationId is null, returns personal connections only.
    /// When organizationId is set, returns that organization's Copilot connections.
    /// </summary>
    public async Task<List<OAuthConnection>> GetCopilotConnectionsAsync(string userId, Guid? organizationId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        
        if (organizationId.HasValue)
        {
            // Organization context: return only org-scoped Copilot connections
            return await db.OAuthConnections
                .Where(c => c.OrganizationId == organizationId.Value && c.Provider == "GitHubCopilot")
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
        else
        {
            // Personal context: return only personal (non-org) Copilot connections
            return await db.OAuthConnections
                .Where(c => c.UserId == userId && c.OrganizationId == null && c.Provider == "GitHubCopilot")
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
    }

    #endregion

    #region Copilot API

    /// <summary>
    /// Calls the Copilot chat completions API.
    /// Uses OpenAI-compatible request/response format.
    /// </summary>
    public async Task<CopilotChatResponse?> ChatCompletionsAsync(
        string copilotToken,
        CopilotChatRequest request)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", copilotToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("Editor-Version", "vscode/1.85.0");
        client.DefaultRequestHeaders.Add("Editor-Plugin-Version", "copilot-chat/0.12.0");
        client.DefaultRequestHeaders.Add("Copilot-Integration-Id", "vscode-chat");

        try
        {
            // Use explicit JsonPropertyName attributes - don't use CamelCase policy which would conflict
            // Also ignore null values to avoid sending empty fields
            var serializerOptions = new JsonSerializerOptions 
            { 
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };
            var jsonContent = JsonSerializer.Serialize(request, serializerOptions);
            
            // Debug log the request (truncated for readability)
            var logContent = jsonContent.Length > 2000 ? jsonContent[..2000] + "..." : jsonContent;
            _logger.LogInformation("Copilot request: {Request}", logContent);
            
            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync(CopilotChatUrl, content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            // Debug log the response
            var logResponse = responseJson.Length > 2000 ? responseJson[..2000] + "..." : responseJson;
            _logger.LogInformation("Copilot response ({StatusCode}): {Response}", response.StatusCode, logResponse);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Copilot chat failed: {StatusCode} - {Response}",
                    response.StatusCode, responseJson);
                return new CopilotChatResponse
                {
                    Error = $"Copilot API error: {response.StatusCode}",
                    ErrorDetails = responseJson
                };
            }

            return JsonSerializer.Deserialize<CopilotChatResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Copilot chat API");
            return new CopilotChatResponse { Error = ex.Message };
        }
    }

    /// <summary>
    /// Gets available models from the Copilot API.
    /// </summary>
    public async Task<List<string>> GetAvailableModelsAsync(string copilotToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", copilotToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var response = await client.GetAsync(CopilotModelsUrl);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get Copilot models: {StatusCode}", response.StatusCode);
                return GetDefaultModels();
            }

            using var doc = JsonDocument.Parse(json);
            var models = new List<string>();

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in data.EnumerateArray())
                {
                    if (model.TryGetProperty("id", out var id))
                    {
                        models.Add(id.GetString() ?? "");
                    }
                }
            }

            return models.Count > 0 ? models : GetDefaultModels();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting Copilot models, using defaults");
            return GetDefaultModels();
        }
    }

    private static List<string> GetDefaultModels() => new()
    {
        "gpt-4.1",
        "gpt-4o",
        "gpt-5-mini",
        "claude-sonnet-4",
        "claude-sonnet-4.5",
        "claude-opus-4.5",
        "claude-haiku-4.5",
        "gemini-2.5-pro",
        "gemini-3-flash-preview"
    };

    #endregion

    #region Helpers

    private async Task<GitHubUserInfo?> GetGitHubUserAsync(string accessToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", accessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("S2GRun/1.0");

        try
        {
            var response = await client.GetAsync("https://api.github.com/user");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GitHubUserInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    #endregion
}

#region DTOs

public class GitHubDeviceCodeResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("interval")]
    public int Interval { get; set; } = 5;
}

public class GitHubTokenPollResult
{
    public TokenPollStatus Status { get; set; }
    public string? AccessToken { get; set; }
    public string? Error { get; set; }
}

public enum TokenPollStatus
{
    Pending,    // User hasn't authorized yet
    SlowDown,   // Polling too fast
    Success,    // Got the token
    Expired,    // Device code expired
    Denied,     // User denied access
    Error       // Other error
}

public class CopilotTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("token")]
    public string Token { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }
    
    // Calculate seconds until expiry
    public int ExpiresIn => (int)(ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}

public class GitHubUserInfo
{
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
}

public class CopilotChatRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o";
    
    [System.Text.Json.Serialization.JsonPropertyName("messages")]
    public List<CopilotChatMessage> Messages { get; set; } = new();
    
    [System.Text.Json.Serialization.JsonPropertyName("tools")]
    public List<CopilotToolDefinition>? Tools { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;
    
    [System.Text.Json.Serialization.JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("temperature")]
    public double? Temperature { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("response_format")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CopilotResponseFormat? ResponseFormat { get; set; }
}

public class CopilotResponseFormat
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "json_object";
}

public class CopilotChatMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("role")]
    public string Role { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("content")]
    public string? Content { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("tool_calls")]
    public List<CopilotToolCall>? ToolCalls { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

public class CopilotToolDefinition
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "function";
    
    [System.Text.Json.Serialization.JsonPropertyName("function")]
    public CopilotFunctionDefinition? Function { get; set; }
}

public class CopilotFunctionDefinition
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}

public class CopilotToolCall
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "function";
    
    [System.Text.Json.Serialization.JsonPropertyName("function")]
    public CopilotFunctionCall? Function { get; set; }
}

public class CopilotFunctionCall
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

public class CopilotChatResponse
{
    public string? Id { get; set; }
    public List<CopilotChatChoice>? Choices { get; set; }
    public CopilotUsage? Usage { get; set; }
    public string? Error { get; set; }
    public string? ErrorDetails { get; set; }
}

public class CopilotChatChoice
{
    public int Index { get; set; }
    public CopilotChatMessage? Message { get; set; }
    public string? FinishReason { get; set; }
}

public class CopilotUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}

#endregion
