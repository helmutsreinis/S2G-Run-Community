using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for tracking and enforcing usage limits per subscription tier
/// </summary>
public class UsageTrackingService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly SubscriptionService _subscriptionService;
    private readonly ILogger<UsageTrackingService> _logger;
    private readonly bool _isSelfHosted;
    
    // In-memory cache for storage limit checks (reduces DB load for high-frequency ingestion)
    private static readonly ConcurrentDictionary<string, StorageLimitCacheEntry> _storageLimitCache = new();
    private static readonly TimeSpan StorageLimitCacheTtl = TimeSpan.FromSeconds(15);
    
    // Plan limits cache (15s TTL)
    private static readonly ConcurrentDictionary<string, (TierLimits Limits, DateTime ExpiresAt)> _planLimitsCache = new();
    private static readonly TimeSpan PlanLimitsCacheTtl = TimeSpan.FromSeconds(15);

    // Fallback tier limits (used when no plan is assigned)
    private static readonly Dictionary<SubscriptionTier, TierLimits> _tierLimits = new()
    {
        [SubscriptionTier.Free] = new TierLimits
        {
            MaxExecutionsPerMonth = 2_000,
            MaxStorageBytes = 50 * 1024 * 1024, // 50MB
            MaxWorkflows = 1,
            MaxVectorDocs = 1000,
            MaxNodesPerWorkflow = 6,
            LogRetentionHours = 4,
            CanImportExport = false,
            CanUseScheduling = false,
            MaxHttpListeners = 1
        },
        [SubscriptionTier.Starter] = new TierLimits
        {
            MaxExecutionsPerMonth = 2_000_000,
            MaxStorageBytes = 500 * 1024 * 1024, // 500MB
            MaxWorkflows = 10,
            MaxVectorDocs = 10000,
            MaxNodesPerWorkflow = 50,
            LogRetentionHours = 7 * 24, // 7 days
            CanImportExport = true,
            CanUseScheduling = true,
            MaxHttpListeners = 5
        }
    };

    public UsageTrackingService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        SubscriptionService subscriptionService,
        ILogger<UsageTrackingService> logger,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _subscriptionService = subscriptionService;
        _logger = logger;
        _isSelfHosted = configuration.GetValue<bool>("SelfHosted");
    }

    /// <summary>
    /// Get or create usage record for user
    /// </summary>
    public async Task<UserUsage> EnsureUsageAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await db.UserUsages.FirstOrDefaultAsync(u => u.UserId == userId);
        
        if (usage == null)
        {
            usage = new UserUsage
            {
                UserId = userId,
                PeriodStart = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            };
            
            db.UserUsages.Add(usage);
            await db.SaveChangesAsync();
        }
        else
        {
            // Check if we need to reset monthly counters
            usage = await ResetMonthlyUsageIfNeededAsync(usage);
        }
        
        return usage;
    }

    /// <summary>
    /// Reset monthly counters if billing period has passed
    /// </summary>
    private async Task<UserUsage> ResetMonthlyUsageIfNeededAsync(UserUsage usage)
    {
        var now = DateTime.UtcNow;
        var monthsSincePeriodStart = (now.Year - usage.PeriodStart.Year) * 12 + 
                                      (now.Month - usage.PeriodStart.Month);
        
        if (monthsSincePeriodStart >= 1)
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            
            var dbUsage = await db.UserUsages.FindAsync(usage.Id);
            if (dbUsage != null)
            {
                dbUsage.ExecutionsThisMonth = 0;
                dbUsage.PeriodStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                dbUsage.LastUpdated = now;
                await db.SaveChangesAsync();
                
                _logger.LogInformation("Reset monthly usage for user {UserId}", usage.UserId);
                return dbUsage;
            }
        }
        
        return usage;
    }

    /// <summary>
    /// Check if user can execute a workflow
    /// </summary>
    public async Task<(bool CanExecute, string? Reason)> CanExecuteAsync(string userId)
    {
        // Self-hosted mode: no execution limits
        if (_isSelfHosted) return (true, null);
        
        var usage = await EnsureUsageAsync(userId);
        var effectiveLimit = await GetEffectiveExecutionLimitAsync(userId, usage);
        
        if (usage.ExecutionsThisMonth >= effectiveLimit)
        {
            return (false, $"Monthly execution limit reached ({effectiveLimit:N0} executions). " +
                          $"Contact admin or upgrade for more executions.");
        }
        
        return (true, null);
    }
    
    /// <summary>
    /// Get effective execution limit for a user (override or tier-based)
    /// </summary>
    public async Task<int> GetEffectiveExecutionLimitAsync(string userId, UserUsage? usage = null)
    {
        usage ??= await EnsureUsageAsync(userId);
        
        // Use override if set
        if (usage.ExecutionLimitOverride.HasValue)
        {
            return usage.ExecutionLimitOverride.Value;
        }
        
        // Otherwise use tier-based limit
        var tier = await _subscriptionService.GetTierAsync(userId);
        var limits = GetLimitsForTier(tier);
        return limits.MaxExecutionsPerMonth;
    }

    /// <summary>
    /// Increment execution count for user. Returns limit info.
    /// </summary>
    public async Task<ExecutionLimitResult> IncrementExecutionCountAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await db.UserUsages.FirstOrDefaultAsync(u => u.UserId == userId);
        if (usage == null)
        {
            usage = new UserUsage
            {
                UserId = userId,
                ExecutionsThisMonth = 1,
                PeriodStart = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            };
            db.UserUsages.Add(usage);
        }
        else
        {
            usage.ExecutionsThisMonth++;
            usage.LastUpdated = DateTime.UtcNow;
        }
        
        await db.SaveChangesAsync();
        
        // Get effective limit
        var effectiveLimit = await GetEffectiveExecutionLimitAsync(userId, usage);
        var limitReached = usage.ExecutionsThisMonth >= effectiveLimit;
        
        if (limitReached)
        {
            _logger.LogWarning("User {UserId} reached execution limit ({Current}/{Limit})", 
                userId, usage.ExecutionsThisMonth, effectiveLimit);
        }
        
        return new ExecutionLimitResult
        {
            CurrentCount = usage.ExecutionsThisMonth,
            Limit = effectiveLimit,
            LimitReached = limitReached
        };
    }

    /// <summary>
    /// Get current usage for user
    /// </summary>
    public async Task<UserUsage> GetUsageAsync(string userId)
    {
        return await EnsureUsageAsync(userId);
    }

    /// <summary>
    /// Get limits for a subscription tier (fallback method)
    /// </summary>
    public TierLimits GetLimitsForTier(SubscriptionTier tier)
    {
        return _tierLimits.TryGetValue(tier, out var limits) 
            ? limits 
            : _tierLimits[SubscriptionTier.Free];
    }

    /// <summary>
    /// Get tier limits for a specific user based on their membership plan (primary) or subscription tier (fallback)
    /// </summary>
    public async Task<TierLimits> GetUserTierLimitsAsync(string userId)
    {
        // Check cache first
        if (_planLimitsCache.TryGetValue(userId, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
        {
            return cached.Limits;
        }
        
        // Try to get from membership plan first
        var plan = await _subscriptionService.GetUserPlanAsync(userId);
        TierLimits limits;
        
        if (plan != null)
        {
            limits = new TierLimits
            {
                MaxExecutionsPerMonth = plan.MaxExecutionsPerMonth,
                MaxStorageBytes = plan.MaxStorageBytes,
                MaxWorkflows = plan.MaxWorkflows,
                MaxVectorDocs = plan.MaxVectorDocs,
                MaxNodesPerWorkflow = plan.MaxNodesPerWorkflow,
                LogRetentionHours = plan.LogRetentionHours,
                CanImportExport = plan.CanImportExport,
                CanUseScheduling = plan.CanUseScheduling,
                MaxHttpListeners = plan.MaxHttpListeners
            };
        }
        else
        {
            // Fallback to tier-based limits
            #pragma warning disable CS0618
            var tier = await _subscriptionService.GetTierAsync(userId);
            limits = GetLimitsForTier(tier);
            #pragma warning restore CS0618
        }
        
        // Cache result
        _planLimitsCache[userId] = (limits, DateTime.UtcNow.Add(PlanLimitsCacheTtl));
        
        return limits;
    }
    
    /// <summary>
    /// Invalidate plan limits cache for a user (call when plan changes)
    /// </summary>
    public void InvalidatePlanLimitsCache(string userId)
    {
        _planLimitsCache.TryRemove(userId, out _);
    }

    /// <summary>
    /// Check if user can add another node to a workflow
    /// </summary>
    public async Task<(bool CanAdd, int CurrentCount, int Limit)> CanAddNodeAsync(string userId, int currentNodeCount)
    {
        var limits = await GetUserTierLimitsAsync(userId);
        // -1 means unlimited
        if (limits.MaxNodesPerWorkflow < 0) return (true, currentNodeCount, -1);
        return (currentNodeCount < limits.MaxNodesPerWorkflow, currentNodeCount, limits.MaxNodesPerWorkflow);
    }

    /// <summary>
    /// Check if user can create another personal workflow
    /// </summary>
    public async Task<(bool CanCreate, int CurrentCount, int Limit)> CanCreateWorkflowAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        // Only count personal workflows (not org workflows)
        var workflowCount = await db.Workflows.CountAsync(w => w.OwnerId == userId && w.OrganizationId == null);
        var limits = await GetUserTierLimitsAsync(userId);
        // -1 means unlimited
        if (limits.MaxWorkflows < 0) return (true, workflowCount, -1);
        return (workflowCount < limits.MaxWorkflows, workflowCount, limits.MaxWorkflows);
    }
    
    /// <summary>
    /// Check if user can add another HTTP listener node across all personal workflows
    /// </summary>
    public async Task<(bool CanAdd, int CurrentCount, int Limit)> CanAddHttpListenerAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        // Count all HttpListener nodes across user's personal workflows (not org workflows)
        var listenerCount = await db.Workflows
            .Where(w => w.OwnerId == userId && w.OrganizationId == null)
            .SelectMany(w => w.Nodes)
            .CountAsync(n => n.NodeType == "HttpListener");
        
        var limits = await GetUserTierLimitsAsync(userId);
        // -1 means unlimited
        if (limits.MaxHttpListeners < 0) return (true, listenerCount, -1);
        return (listenerCount < limits.MaxHttpListeners, listenerCount, limits.MaxHttpListeners);
    }
    
    /// <summary>
    /// Check if user can add another vector document based on MaxVectorDocs limit
    /// </summary>
    public async Task<(bool CanAdd, int CurrentCount, int Limit)> CanAddVectorDocAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        // Count all vector documents for this user
        var docCount = await db.VectorDocuments.CountAsync(v => v.UserId == userId);
        
        var limits = await GetUserTierLimitsAsync(userId);
        // -1 means unlimited
        if (limits.MaxVectorDocs < 0) return (true, docCount, -1);
        return (docCount < limits.MaxVectorDocs, docCount, limits.MaxVectorDocs);
    }

    /// <summary>
    /// Get usage percentage for display
    /// </summary>
    public async Task<UsagePercentages> GetUsagePercentagesAsync(string userId)
    {
        var usage = await GetUsageAsync(userId);
        var limits = await GetUserTierLimitsAsync(userId);
        
        // Use overrides if set
        var effectiveExecutionLimit = usage.ExecutionLimitOverride ?? limits.MaxExecutionsPerMonth;
        var effectiveStorageLimit = usage.StorageLimitOverride ?? limits.MaxStorageBytes;
        
        // Get vector docs and personal workflows count (exclude org workflows)
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var vectorDocsCount = await db.VectorDocuments.CountAsync(v => v.UserId == userId);
        var workflowsCount = await db.Workflows.CountAsync(w => w.OwnerId == userId && w.OrganizationId == null);
        
        // Calculate percentages (handle unlimited limits with -1)
        var vectorDocsPercent = limits.MaxVectorDocs < 0 ? 0 : 
            Math.Min(100, (double)vectorDocsCount / limits.MaxVectorDocs * 100);
        var workflowsPercent = limits.MaxWorkflows < 0 ? 0 : 
            Math.Min(100, (double)workflowsCount / limits.MaxWorkflows * 100);
        
        return new UsagePercentages
        {
            ExecutionsUsed = usage.ExecutionsThisMonth,
            ExecutionsLimit = effectiveExecutionLimit,
            ExecutionsPercent = Math.Min(100, (double)usage.ExecutionsThisMonth / effectiveExecutionLimit * 100),
            
            StorageUsed = usage.TotalStorageBytes,
            StorageLimit = effectiveStorageLimit,
            StoragePercent = Math.Min(100, (double)usage.TotalStorageBytes / effectiveStorageLimit * 100),
            
            VectorDocsUsed = vectorDocsCount,
            VectorDocsLimit = limits.MaxVectorDocs,
            VectorDocsPercent = vectorDocsPercent,
            
            WorkflowsUsed = workflowsCount,
            WorkflowsLimit = limits.MaxWorkflows,
            WorkflowsPercent = workflowsPercent
        };
    }

    /// <summary>
    /// Update storage metrics when data changes. Returns limit info.
    /// </summary>
    public async Task<StorageLimitResult> UpdateStorageAsync(string userId, long workflowBytes = 0, long logBytes = 0, 
                                          long vectorBytes = 0, long tableBytes = 0, long blobStorageBytes = 0)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await db.UserUsages.FirstOrDefaultAsync(u => u.UserId == userId);
        if (usage == null)
        {
            usage = new UserUsage { UserId = userId };
            db.UserUsages.Add(usage);
        }
        
        usage.StorageBytesWorkflows += workflowBytes;
        usage.StorageBytesLogs += logBytes;
        usage.StorageBytesVectors += vectorBytes;
        usage.StorageBytesTables += tableBytes;
        usage.StorageBytesBlobStorage += blobStorageBytes;
        
        // Clamp to zero (estimates may not match perfectly)
        if (usage.StorageBytesWorkflows < 0) usage.StorageBytesWorkflows = 0;
        if (usage.StorageBytesLogs < 0) usage.StorageBytesLogs = 0;
        if (usage.StorageBytesVectors < 0) usage.StorageBytesVectors = 0;
        if (usage.StorageBytesTables < 0) usage.StorageBytesTables = 0;
        if (usage.StorageBytesBlobStorage < 0) usage.StorageBytesBlobStorage = 0;
        
        usage.LastUpdated = DateTime.UtcNow;
        
        await db.SaveChangesAsync();
        
        // Check limit
        var effectiveLimit = await GetEffectiveStorageLimitAsync(userId, usage);
        var limitReached = usage.TotalStorageBytes >= effectiveLimit;
        
        if (limitReached)
        {
            _logger.LogWarning("User {UserId} reached storage limit ({Current}/{Limit} bytes)", 
                userId, usage.TotalStorageBytes, effectiveLimit);
        }
        
        return new StorageLimitResult
        {
            CurrentBytes = usage.TotalStorageBytes,
            LimitBytes = effectiveLimit,
            LimitReached = limitReached
        };
    }
    
    /// <summary>
    /// Check if user can store more data (uses in-memory cache to reduce DB load)
    /// </summary>
    public async Task<(bool CanStore, string? Reason)> CanStoreAsync(string userId)
    {
        // Self-hosted mode: no storage limits
        if (_isSelfHosted) return (true, null);
        
        // Check cache first
        if (_storageLimitCache.TryGetValue(userId, out var cacheEntry) && 
            DateTime.UtcNow < cacheEntry.ExpiresAt)
        {
            return (cacheEntry.CanStore, cacheEntry.Reason);
        }
        
        // Cache miss or expired - fetch from DB
        var usage = await EnsureUsageAsync(userId);
        var effectiveLimit = await GetEffectiveStorageLimitAsync(userId, usage);
        
        bool canStore = usage.TotalStorageBytes < effectiveLimit;
        string? reason = canStore ? null : 
            $"Storage limit reached ({FormatBytes(effectiveLimit)}). Contact admin or upgrade for more storage.";
        
        // Cache the result
        _storageLimitCache[userId] = new StorageLimitCacheEntry
        {
            CanStore = canStore,
            Reason = reason,
            ExpiresAt = DateTime.UtcNow.Add(StorageLimitCacheTtl)
        };
        
        return (canStore, reason);
    }
    
    /// <summary>
    /// Invalidate storage limit cache for a user (call after storage operations)
    /// </summary>
    public void InvalidateStorageLimitCache(string userId)
    {
        _storageLimitCache.TryRemove(userId, out _);
    }
    
    /// <summary>
    /// Get effective storage limit for a user (override or tier-based)
    /// </summary>
    public async Task<long> GetEffectiveStorageLimitAsync(string userId, UserUsage? usage = null)
    {
        usage ??= await EnsureUsageAsync(userId);
        
        // Use override if set
        if (usage.StorageLimitOverride.HasValue)
        {
            return usage.StorageLimitOverride.Value;
        }
        
        // Otherwise use tier-based limit
        var tier = await _subscriptionService.GetTierAsync(userId);
        var limits = GetLimitsForTier(tier);
        return limits.MaxStorageBytes;
    }
    
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
    
    /// <summary>
    /// Set or clear execution limit override for a user (admin function)
    /// </summary>
    public async Task SetExecutionLimitOverrideAsync(string userId, int? limitOverride)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await db.UserUsages.FirstOrDefaultAsync(u => u.UserId == userId);
        if (usage == null)
        {
            usage = new UserUsage { UserId = userId };
            db.UserUsages.Add(usage);
        }
        
        usage.ExecutionLimitOverride = limitOverride;
        usage.LastUpdated = DateTime.UtcNow;
        
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Set execution limit override for user {UserId} to {Limit}", 
            userId, limitOverride?.ToString() ?? "tier default");
    }
    
    /// <summary>
    /// Set or clear storage limit override for a user (admin function)
    /// </summary>
    public async Task SetStorageLimitOverrideAsync(string userId, long? limitOverride)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await db.UserUsages.FirstOrDefaultAsync(u => u.UserId == userId);
        if (usage == null)
        {
            usage = new UserUsage { UserId = userId };
            db.UserUsages.Add(usage);
        }
        
        usage.StorageLimitOverride = limitOverride;
        usage.LastUpdated = DateTime.UtcNow;
        
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Set storage limit override for user {UserId} to {Limit}", 
            userId, limitOverride.HasValue ? FormatBytes(limitOverride.Value) : "tier default");
    }
}

/// <summary>
/// Result of storage update with limit info
/// </summary>
public class StorageLimitResult
{
    public long CurrentBytes { get; set; }
    public long LimitBytes { get; set; }
    public bool LimitReached { get; set; }
}

/// <summary>
/// Result of execution increment with limit info
/// </summary>
public class ExecutionLimitResult
{
    public int CurrentCount { get; set; }
    public int Limit { get; set; }
    public bool LimitReached { get; set; }
}

/// <summary>
/// Tier limit definitions
/// </summary>
public class TierLimits
{
    public int MaxExecutionsPerMonth { get; set; }
    public long MaxStorageBytes { get; set; }
    public int MaxWorkflows { get; set; }
    public int MaxVectorDocs { get; set; }
    public int MaxNodesPerWorkflow { get; set; }
    public int LogRetentionHours { get; set; }
    public bool CanImportExport { get; set; }
    public bool CanUseScheduling { get; set; }
    public int MaxHttpListeners { get; set; }
}

/// <summary>
/// Usage percentages for display
/// </summary>
public class UsagePercentages
{
    public int ExecutionsUsed { get; set; }
    public int ExecutionsLimit { get; set; }
    public double ExecutionsPercent { get; set; }
    
    public long StorageUsed { get; set; }
    public long StorageLimit { get; set; }
    public double StoragePercent { get; set; }
    
    public int VectorDocsUsed { get; set; }
    public int VectorDocsLimit { get; set; }
    public double VectorDocsPercent { get; set; }
    
    public int WorkflowsUsed { get; set; }
    public int WorkflowsLimit { get; set; }
    public double WorkflowsPercent { get; set; }
}

/// <summary>
/// Cache entry for storage limit checks
/// </summary>
public class StorageLimitCacheEntry
{
    public bool CanStore { get; set; }
    public string? Reason { get; set; }
    public DateTime ExpiresAt { get; set; }
}

