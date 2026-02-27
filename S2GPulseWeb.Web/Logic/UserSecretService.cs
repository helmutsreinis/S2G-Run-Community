using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

public class UserSecretService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public UserSecretService(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Gets a secret value in personal or organization context.
    /// No fallback between contexts - strict isolation.
    /// </summary>
    public async Task<string?> GetSecretAsync(string userId, string name, Guid? organizationId = null)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        
        if (organizationId.HasValue)
        {
            // Organization context - only org secrets
            var orgSecret = await context.UserSecrets
                .FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.Name == name);
            return orgSecret?.Value;
        }
        
        // Personal context - only personal secrets
        var secret = await context.UserSecrets
            .FirstOrDefaultAsync(s => s.UserId == userId && s.OrganizationId == null && s.Name == name);
        return secret?.Value;
    }

    /// <summary>
    /// Sets a secret in personal or organization context.
    /// </summary>
    public async Task SetSecretAsync(string userId, string name, string value, Guid? organizationId = null)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        
        UserSecret? secret;
        if (organizationId.HasValue)
        {
            // Organization secret - keyed by org + name
            secret = await context.UserSecrets
                .FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.Name == name);
        }
        else
        {
            // Personal secret - keyed by user + name + null org
            secret = await context.UserSecrets
                .FirstOrDefaultAsync(s => s.UserId == userId && s.OrganizationId == null && s.Name == name);
        }

        if (secret == null)
        {
            secret = new UserSecret
            {
                UserId = userId,
                Name = name,
                Value = value,
                OrganizationId = organizationId,
                UpdatedAt = DateTime.UtcNow
            };
            context.UserSecrets.Add(secret);
        }
        else
        {
            secret.Value = value;
            secret.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Gets all secrets for a user in personal or organization context.
    /// </summary>
    public async Task<List<UserSecret>> GetUserSecretsAsync(string userId, Guid? organizationId = null)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        
        if (organizationId.HasValue)
        {
            return await context.UserSecrets
                .Where(s => s.OrganizationId == organizationId)
                .OrderBy(s => s.Name)
                .ToListAsync();
        }
        
        return await context.UserSecrets
            .Where(s => s.UserId == userId && s.OrganizationId == null)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Deletes a secret in personal or organization context.
    /// </summary>
    public async Task DeleteSecretAsync(string userId, string name, Guid? organizationId = null)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        
        UserSecret? secret;
        if (organizationId.HasValue)
        {
            secret = await context.UserSecrets
                .FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.Name == name);
        }
        else
        {
            secret = await context.UserSecrets
                .FirstOrDefaultAsync(s => s.UserId == userId && s.OrganizationId == null && s.Name == name);
        }
        
        if (secret != null)
        {
            context.UserSecrets.Remove(secret);
            await context.SaveChangesAsync();
        }
    }
}
