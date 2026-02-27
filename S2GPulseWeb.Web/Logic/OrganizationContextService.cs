using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Scoped service to manage the current organization context per user session.
/// Tracks whether a user is operating in their personal space or on behalf of an organization.
/// </summary>
public class OrganizationContextService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public OrganizationContextService(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Get the current active organization ID for a user.
    /// Returns null if user is in personal context.
    /// </summary>
    public async Task<Guid?> GetCurrentOrganizationIdAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var preference = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (preference?.ActiveOrganizationId == null)
            return null;
        
        // Verify the organization still exists and user is still a member
        var orgId = preference.ActiveOrganizationId.Value;
        var isMember = await context.OrganizationMembers
            .AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId);
        
        if (!isMember)
        {
            // Clear invalid context
            preference.ActiveOrganizationId = null;
            preference.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            return null;
        }
        
        return orgId;
    }

    /// <summary>
    /// Get the full context information including organization details.
    /// </summary>
    public async Task<OrganizationContext> GetCurrentContextAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var preference = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (preference?.ActiveOrganizationId == null)
        {
            return OrganizationContext.Personal();
        }
        
        var orgId = preference.ActiveOrganizationId.Value;
        
        // Get organization with user's membership
        var membership = await context.OrganizationMembers
            .Include(m => m.Organization)
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == userId);
        
        if (membership == null || !membership.Organization.IsActive)
        {
            // Clear invalid context
            preference.ActiveOrganizationId = null;
            preference.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            return OrganizationContext.Personal();
        }
        
        return new OrganizationContext
        {
            IsPersonal = false,
            OrganizationId = orgId,
            OrganizationName = membership.Organization.Name,
            SvgLogo = membership.Organization.SvgLogo,
            UserRole = membership.Role
        };
    }

    /// <summary>
    /// Switch to an organization context.
    /// Verifies user is a member before switching.
    /// </summary>
    public async Task<bool> SetCurrentOrganizationAsync(string userId, Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Verify membership
        var isMember = await context.OrganizationMembers
            .AnyAsync(m => m.OrganizationId == orgId && 
                          m.UserId == userId && 
                          m.Organization.IsActive);
        
        if (!isMember)
            return false;
        
        var preference = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (preference == null)
        {
            preference = new UserPreference
            {
                UserId = userId,
                ActiveOrganizationId = orgId,
                UpdatedAt = DateTime.UtcNow
            };
            context.UserPreferences.Add(preference);
        }
        else
        {
            preference.ActiveOrganizationId = orgId;
            preference.UpdatedAt = DateTime.UtcNow;
        }
        
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Clear organization context, returning to personal mode.
    /// </summary>
    public async Task ClearOrganizationContextAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var preference = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (preference != null)
        {
            preference.ActiveOrganizationId = null;
            preference.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Check if user is currently in an organization context.
    /// </summary>
    public async Task<bool> IsInOrganizationContextAsync(string userId)
    {
        var orgId = await GetCurrentOrganizationIdAsync(userId);
        return orgId.HasValue;
    }
}

/// <summary>
/// DTO representing the current organization context for a user.
/// </summary>
public class OrganizationContext
{
    public bool IsPersonal { get; set; } = true;
    public Guid? OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
    public string? SvgLogo { get; set; }
    public OrganizationRole? UserRole { get; set; }

    public static OrganizationContext Personal() => new() { IsPersonal = true };
}
