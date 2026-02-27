using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// REST API for managing user's OAuth connections (list, create, update, delete).
/// </summary>
[ApiController]
[Route("api/v1/connections")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class ConnectionApiController : ControllerBase
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly OAuthService _oauthService;

    public ConnectionApiController(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        OAuthService oauthService)
    {
        _dbContextFactory = dbContextFactory;
        _oauthService = oauthService;
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>List all OAuth connections for the authenticated user's active context.</summary>
    [HttpGet]
    public async Task<IActionResult> ListConnections()
    {
        var userId = GetUserId();
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Determine user's active organization context
        var pref = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        var orgId = pref?.ActiveOrganizationId;

        var query = context.OAuthConnections
            .Where(c => c.UserId == userId);

        if (orgId.HasValue)
            query = query.Where(c => c.OrganizationId == orgId);
        else
            query = query.Where(c => c.OrganizationId == null);

        var connections = await query
            .Select(c => new
            {
                c.Id,
                c.Provider,
                c.Email,
                c.ConnectionName,
                c.CreatedAt,
                c.LastUsedAt,
                c.OrganizationId,
                hasPlatformConnector = c.PlatformConnectorId != null
            })
            .OrderBy(c => c.Provider)
            .ToListAsync();

        return Ok(connections);
    }

    /// <summary>Get details for a specific connection (no tokens exposed).</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetConnection(Guid id)
    {
        var userId = GetUserId();
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var connection = await context.OAuthConnections
            .Where(c => c.Id == id && c.UserId == userId)
            .Select(c => new
            {
                c.Id,
                c.Provider,
                c.Email,
                c.ConnectionName,
                c.CreatedAt,
                c.LastUsedAt,
                c.OrganizationId,
                hasPlatformConnector = c.PlatformConnectorId != null
            })
            .FirstOrDefaultAsync();

        if (connection == null) return NotFound(new { error = "Connection not found." });
        return Ok(connection);
    }

    /// <summary>Create a new OAuth connection with raw tokens.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateConnection([FromBody] CreateConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
            return BadRequest(new { error = "Provider is required." });
        if (string.IsNullOrWhiteSpace(request.ConnectionName))
            return BadRequest(new { error = "ConnectionName is required." });
        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return BadRequest(new { error = "AccessToken is required." });

        var userId = GetUserId();
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Determine user's active organization context
        var pref = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        var orgId = pref?.ActiveOrganizationId;

        var connection = new OAuthConnection
        {
            UserId = userId,
            Provider = request.Provider,
            ConnectionName = request.ConnectionName,
            AccessToken = request.AccessToken,
            RefreshToken = request.RefreshToken ?? "",
            TokenExpiry = request.TokenExpiry ?? DateTime.UtcNow.AddHours(1),
            Scopes = request.Scopes ?? "",
            TenantId = request.TenantId,
            Email = request.Email,
            OrganizationId = orgId
        };

        context.OAuthConnections.Add(connection);
        await context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetConnection), new { id = connection.Id }, new
        {
            connection.Id,
            connection.Provider,
            connection.Email,
            connection.ConnectionName,
            connection.CreatedAt,
            connection.OrganizationId,
            hasPlatformConnector = false
        });
    }

    /// <summary>Update a connection's name and/or tokens.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateConnection(Guid id, [FromBody] UpdateConnectionRequest request)
    {
        var userId = GetUserId();
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var connection = await context.OAuthConnections
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (connection == null) return NotFound(new { error = "Connection not found." });

        if (!string.IsNullOrWhiteSpace(request.ConnectionName))
            connection.ConnectionName = request.ConnectionName;

        if (!string.IsNullOrWhiteSpace(request.AccessToken))
            connection.AccessToken = request.AccessToken;

        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            connection.RefreshToken = request.RefreshToken;

        if (request.TokenExpiry.HasValue)
            connection.TokenExpiry = request.TokenExpiry.Value;

        if (!string.IsNullOrWhiteSpace(request.Email))
            connection.Email = request.Email;

        await context.SaveChangesAsync();

        return Ok(new
        {
            connection.Id,
            connection.Provider,
            connection.Email,
            connection.ConnectionName,
            connection.CreatedAt,
            connection.LastUsedAt,
            connection.OrganizationId,
            hasPlatformConnector = connection.PlatformConnectorId != null
        });
    }

    /// <summary>Delete an OAuth connection.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteConnection(Guid id)
    {
        var userId = GetUserId();
        var deleted = await _oauthService.DeleteConnectionAsync(id, userId);
        if (!deleted) return NotFound(new { error = "Connection not found." });
        return Ok(new { message = "Connection deleted." });
    }
}

#region Connection API DTOs

public class CreateConnectionRequest
{
    public string Provider { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTime? TokenExpiry { get; set; }
    public string? Scopes { get; set; }
    public string? TenantId { get; set; }
    public string? Email { get; set; }
}

public class UpdateConnectionRequest
{
    public string? ConnectionName { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? TokenExpiry { get; set; }
    public string? Email { get; set; }
}

#endregion
