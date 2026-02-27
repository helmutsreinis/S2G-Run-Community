using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing persisted node execution logs
/// </summary>
public class NodeLogService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly UsageTrackingService _usageTrackingService;

    public NodeLogService(IDbContextFactory<ApplicationDbContext> dbContextFactory, UsageTrackingService usageTrackingService)
    {
        _dbContextFactory = dbContextFactory;
        _usageTrackingService = usageTrackingService;
    }

    /// <summary>
    /// Save a log entry to the database
    /// </summary>
    public async Task SaveLogAsync(NodeLog log)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        context.NodeLogs.Add(log);
        await context.SaveChangesAsync();
        
        // Track storage usage
        var logBytes = EstimateLogBytes(log);
        await _usageTrackingService.UpdateStorageAsync(log.UserId, logBytes: logBytes);
    }

    /// <summary>
    /// Save multiple log entries to the database
    /// </summary>
    public async Task SaveLogsAsync(IEnumerable<NodeLog> logs)
    {
        var logList = logs.ToList();
        if (!logList.Any()) return;
        
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        context.NodeLogs.AddRange(logList);
        await context.SaveChangesAsync();
        
        // Track storage usage
        var userId = logList.First().UserId;
        var totalBytes = logList.Sum(l => EstimateLogBytes(l));
        await _usageTrackingService.UpdateStorageAsync(userId, logBytes: totalBytes);
    }
    
    private static long EstimateLogBytes(NodeLog log)
    {
        // Estimate bytes: message + detail + metadata overhead
        var bytes = Encoding.UTF8.GetByteCount(log.Message ?? "");
        bytes += Encoding.UTF8.GetByteCount(log.Detail ?? "");
        bytes += Encoding.UTF8.GetByteCount(log.NodeName ?? "");
        bytes += Encoding.UTF8.GetByteCount(log.NodeType ?? "");
        bytes += 100; // Overhead for other fields (IDs, timestamps, etc.)
        return bytes;
    }

    /// <summary>
    /// Query logs with filters and pagination
    /// </summary>
    public async Task<(List<NodeLog> Logs, int TotalCount)> GetLogsAsync(
        string userId,
        Guid? nodeId = null,
        Guid? workflowId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? searchText = null,
        NodeLogLevel? level = null,
        int page = 1,
        int pageSize = 50)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var query = context.NodeLogs
            .Where(l => l.UserId == userId)
            .AsQueryable();

        if (nodeId.HasValue)
            query = query.Where(l => l.NodeId == nodeId.Value);

        if (workflowId.HasValue)
            query = query.Where(l => l.WorkflowId == workflowId.Value);

        if (dateFrom.HasValue)
            query = query.Where(l => l.Timestamp >= dateFrom.Value);

        if (dateTo.HasValue)
            query = query.Where(l => l.Timestamp <= dateTo.Value);

        if (!string.IsNullOrWhiteSpace(searchText))
            query = query.Where(l => l.Message.Contains(searchText) || 
                                     (l.Detail != null && l.Detail.Contains(searchText)) ||
                                     l.NodeName.Contains(searchText));

        if (level.HasValue)
            query = query.Where(l => l.Level == level.Value);

        var totalCount = await query.CountAsync();

        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (logs, totalCount);
    }

    /// <summary>
    /// Clear logs for a user, optionally filtered by node. Returns count and bytes freed.
    /// </summary>
    public async Task<(int Count, long BytesFreed)> ClearLogsAsync(string userId, Guid? nodeId = null, Guid? workflowId = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var query = context.NodeLogs.Where(l => l.UserId == userId);

        if (nodeId.HasValue)
            query = query.Where(l => l.NodeId == nodeId.Value);

        if (workflowId.HasValue)
            query = query.Where(l => l.WorkflowId == workflowId.Value);

        var logsToDelete = await query.ToListAsync();
        
        // Calculate bytes to free
        long bytesFreed = logsToDelete.Sum(l => EstimateLogBytes(l));
        
        context.NodeLogs.RemoveRange(logsToDelete);
        await context.SaveChangesAsync();
        
        // Update storage tracking (negative to reduce)
        if (logsToDelete.Any())
        {
            await _usageTrackingService.UpdateStorageAsync(userId, logBytes: -bytesFreed);
        }
        
        return (logsToDelete.Count, bytesFreed);
    }

    /// <summary>
    /// Delete logs older than the specified cutoff date
    /// </summary>
    public async Task<int> DeleteLogsOlderThanAsync(string userId, DateTime cutoffDate)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var logsToDelete = await context.NodeLogs
            .Where(l => l.UserId == userId && l.Timestamp < cutoffDate)
            .ToListAsync();

        context.NodeLogs.RemoveRange(logsToDelete);
        await context.SaveChangesAsync();

        return logsToDelete.Count;
    }

    /// <summary>
    /// Get retention setting for a user
    /// </summary>
    public async Task<LogRetentionSetting?> GetRetentionSettingAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.LogRetentionSettings
            .FirstOrDefaultAsync(s => s.UserId == userId);
    }

    /// <summary>
    /// Set retention setting for a user. Value will be clamped to tier-based maximum.
    /// </summary>
    public async Task<(int ClampedValue, RetentionUnit ClampedUnit, bool WasClamped)> SetRetentionSettingAsync(string userId, int value, RetentionUnit unit)
    {
        // Get tier limits to enforce max retention
        var tierLimits = await _usageTrackingService.GetUserTierLimitsAsync(userId);
        var maxRetentionHours = tierLimits.LogRetentionHours;
        
        // Convert requested value to hours for comparison
        var requestedHours = unit switch
        {
            RetentionUnit.Minutes => value / 60.0,
            RetentionUnit.Hours => value,
            RetentionUnit.Days => value * 24,
            _ => value
        };
        
        var wasClamped = false;
        if (requestedHours > maxRetentionHours)
        {
            // Clamp to tier max in hours
            value = maxRetentionHours;
            unit = RetentionUnit.Hours;
            wasClamped = true;
        }
        
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var existing = await context.LogRetentionSettings
            .FirstOrDefaultAsync(s => s.UserId == userId);

        if (existing != null)
        {
            existing.RetentionValue = value;
            existing.RetentionUnit = unit;
        }
        else
        {
            context.LogRetentionSettings.Add(new LogRetentionSetting
            {
                UserId = userId,
                RetentionValue = value,
                RetentionUnit = unit
            });
        }

        await context.SaveChangesAsync();
        return (value, unit, wasClamped);
    }

    /// <summary>
    /// Get all retention settings (for background cleanup service)
    /// </summary>
    public async Task<List<LogRetentionSetting>> GetAllRetentionSettingsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.LogRetentionSettings.ToListAsync();
    }

    /// <summary>
    /// Calculate cutoff date based on retention setting
    /// </summary>
    public static DateTime CalculateCutoffDate(LogRetentionSetting setting)
    {
        return setting.RetentionUnit switch
        {
            RetentionUnit.Minutes => DateTime.UtcNow.AddMinutes(-setting.RetentionValue),
            RetentionUnit.Hours => DateTime.UtcNow.AddHours(-setting.RetentionValue),
            RetentionUnit.Days => DateTime.UtcNow.AddDays(-setting.RetentionValue),
            _ => DateTime.UtcNow.AddDays(-7) // Default to 7 days
        };
    }
}
