using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Extracts a valid OAuth access token from an active connector.
/// Automatically refreshes expired tokens before returning.
/// </summary>
public class ConnectorTokenNode : BaseNodeExecutor
{
    private readonly OAuthService _oAuthService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public ConnectorTokenNode(
        NodeExecutionManager executionManager, 
        OAuthService oAuthService,
        IDbContextFactory<ApplicationDbContext> dbContextFactory)
        : base(executionManager)
    {
        _oAuthService = oAuthService;
        _dbContextFactory = dbContextFactory;
    }

    public override string NodeType => "ConnectorToken";

    public override List<string> GetOutputParameters() => new()
    {
        "AccessToken",
        "Provider",
        "Email",
        "Scopes",
        "TokenExpiry",
        "Success",
        "ErrorMessage"
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<ConnectorTokenConfig>(node.Configuration ?? "{}") ?? new();

        // Validate connection ID
        if (string.IsNullOrEmpty(config.ConnectionId))
        {
            Log(node, NodeLogLevel.Error, "No connector selected");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "No connector selected. Please configure the node with an active connection.",
                OutputData = new Dictionary<string, object?>
                {
                    ["Success"] = false,
                    ["ErrorMessage"] = "No connector selected"
                }
            };
        }

        if (!Guid.TryParse(config.ConnectionId, out var connectionId))
        {
            Log(node, NodeLogLevel.Error, "Invalid connection ID format");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Invalid connection ID format",
                OutputData = new Dictionary<string, object?>
                {
                    ["Success"] = false,
                    ["ErrorMessage"] = "Invalid connection ID format"
                }
            };
        }

        Log(node, NodeLogLevel.Info, "Retrieving access token from connector...");

        try
        {
            // Get valid access token (auto-refreshes if expired)
            var accessToken = await _oAuthService.GetValidAccessTokenAsync(connectionId);

            if (string.IsNullOrEmpty(accessToken))
            {
                Log(node, NodeLogLevel.Error, "Failed to retrieve access token. Connection may be expired or revoked.");
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to retrieve access token. The connection may be expired or revoked. Please reconnect in the Connections page.",
                    OutputData = new Dictionary<string, object?>
                    {
                        ["Success"] = false,
                        ["ErrorMessage"] = "Failed to retrieve access token - connection expired or revoked"
                    }
                };
            }

            // Get connection metadata
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var connection = await db.OAuthConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == connectionId);

            if (connection == null)
            {
                Log(node, NodeLogLevel.Error, "Connection not found in database");
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Connection not found",
                    OutputData = new Dictionary<string, object?>
                    {
                        ["Success"] = false,
                        ["ErrorMessage"] = "Connection not found"
                    }
                };
            }

            Log(node, NodeLogLevel.Info, $"Access token retrieved successfully for {connection.Email ?? connection.Provider}");

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    ["AccessToken"] = accessToken,
                    ["Provider"] = connection.Provider,
                    ["Email"] = connection.Email,
                    ["Scopes"] = connection.Scopes,
                    ["TokenExpiry"] = connection.TokenExpiry.ToString("o"),
                    ["Success"] = true,
                    ["ErrorMessage"] = null
                }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Error retrieving access token: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                OutputData = new Dictionary<string, object?>
                {
                    ["Success"] = false,
                    ["ErrorMessage"] = ex.Message
                }
            };
        }
    }
}

/// <summary>
/// Configuration for ConnectorToken node.
/// </summary>
public class ConnectorTokenConfig
{
    /// <summary>Connection GUID from the OAuth connections.</summary>
    public string? ConnectionId { get; set; }
}
