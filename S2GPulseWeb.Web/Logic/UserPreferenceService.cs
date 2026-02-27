using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing user preferences like last opened workflow
/// </summary>
public class UserPreferenceService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public UserPreferenceService(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Get the last opened workflow ID for a user
    /// </summary>
    public async Task<Guid?> GetLastWorkflowAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var pref = await context.UserPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        return pref?.LastWorkflowId;
    }

    /// <summary>
    /// Set the last opened workflow for a user
    /// </summary>
    public async Task SetLastWorkflowAsync(string userId, Guid? workflowId)
    {
        if (string.IsNullOrEmpty(userId)) return;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var pref = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (pref == null)
        {
            pref = new UserPreference
            {
                UserId = userId,
                LastWorkflowId = workflowId,
                UpdatedAt = DateTime.UtcNow
            };
            context.UserPreferences.Add(pref);
        }
        else
        {
            pref.LastWorkflowId = workflowId;
            pref.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Get the designated Copilot connection ID for AI Builder
    /// </summary>
    public async Task<Guid?> GetAiBuilderCopilotConnectionAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var pref = await context.UserPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);
        
        return pref?.AiBuilderCopilotConnectionId;
    }

    /// <summary>
    /// Set the designated Copilot connection for AI Builder
    /// </summary>
    public async Task SetAiBuilderCopilotConnectionAsync(string userId, Guid? connectionId)
    {
        if (string.IsNullOrEmpty(userId)) return;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var pref = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (pref == null)
        {
            pref = new UserPreference
            {
                UserId = userId,
                AiBuilderCopilotConnectionId = connectionId,
                UpdatedAt = DateTime.UtcNow
            };
            context.UserPreferences.Add(pref);
        }
        else
        {
            pref.AiBuilderCopilotConnectionId = connectionId;
            pref.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }
}
