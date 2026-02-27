using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Tracks usage metrics per organization, mirroring UsageTrackingService for personal accounts.
/// Provides quota enforcement for organization-specific limits.
/// </summary>
public class OrganizationUsageTrackingService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public OrganizationUsageTrackingService(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Get or create usage tracking record for an organization.
    /// Resets monthly counters if period has expired.
    /// </summary>
    public async Task<OrganizationUsage> EnsureUsageAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await context.OrganizationUsages
            .FirstOrDefaultAsync(u => u.OrganizationId == orgId);
        
        if (usage == null)
        {
            usage = new OrganizationUsage
            {
                OrganizationId = orgId,
                PeriodStart = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            };
            context.OrganizationUsages.Add(usage);
            await context.SaveChangesAsync();
        }
        else if (IsNewBillingPeriod(usage.PeriodStart))
        {
            // Reset monthly counters
            usage.ExecutionsThisMonth = 0;
            usage.PeriodStart = DateTime.UtcNow;
            usage.LastUpdated = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
        
        return usage;
    }

    private static bool IsNewBillingPeriod(DateTime periodStart)
    {
        var now = DateTime.UtcNow;
        return now.Year > periodStart.Year || 
               (now.Year == periodStart.Year && now.Month > periodStart.Month);
    }

    /// <summary>
    /// Get the organization's plan-based limits from founder's membership.
    /// </summary>
    public async Task<OrganizationLimits> GetOrgLimitsAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var org = await context.Organizations
            .FirstOrDefaultAsync(o => o.Id == orgId);
        
        if (org == null)
            return new OrganizationLimits();
        
        var subscription = await context.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == org.FounderId);
        
        var plan = subscription?.MembershipPlan;
        if (plan == null)
            return new OrganizationLimits();
        
        return new OrganizationLimits
        {
            MaxWorkflows = plan.MaxWorkflowsPerOrganization,
            MaxNodesPerWorkflow = plan.MaxNodesPerOrgWorkflow,
            MaxExecutionsPerMonth = plan.MaxOrgExecutionsPerMonth,
            MaxStorageBytes = plan.MaxOrgStorageBytes,
            MaxVectorDocs = plan.MaxOrgVectorDocs,
            MaxMembersPerOrganization = plan.MaxMembersPerOrganization
        };
    }

    #region Quota Checks

    /// <summary>
    /// Check if organization can execute another workflow.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanExecuteAsync(Guid orgId)
    {
        var usage = await EnsureUsageAsync(orgId);
        var limits = await GetOrgLimitsAsync(orgId);
        
        if (limits.MaxExecutionsPerMonth <= 0) // 0 = unlimited
            return (true, null);
        
        if (usage.ExecutionsThisMonth >= limits.MaxExecutionsPerMonth)
        {
            return (false, $"Organization has reached the monthly execution limit of {limits.MaxExecutionsPerMonth}");
        }
        
        return (true, null);
    }

    /// <summary>
    /// Increment the execution counter for an organization.
    /// </summary>
    public async Task IncrementExecutionCountAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await context.OrganizationUsages
            .FirstOrDefaultAsync(u => u.OrganizationId == orgId);
        
        if (usage != null)
        {
            usage.ExecutionsThisMonth++;
            usage.LastUpdated = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Check if organization can store more data.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanStoreAsync(Guid orgId, long additionalBytes = 0)
    {
        var usage = await EnsureUsageAsync(orgId);
        var limits = await GetOrgLimitsAsync(orgId);
        
        if (limits.MaxStorageBytes <= 0) // 0 = unlimited
            return (true, null);
        
        if (usage.TotalStorageBytes + additionalBytes > limits.MaxStorageBytes)
        {
            var usedMB = usage.TotalStorageBytes / (1024 * 1024);
            var limitMB = limits.MaxStorageBytes / (1024 * 1024);
            return (false, $"Organization storage limit reached ({usedMB}MB / {limitMB}MB)");
        }
        
        return (true, null);
    }

    /// <summary>
    /// Update storage metrics for an organization.
    /// </summary>
    public async Task UpdateStorageAsync(
        Guid orgId,
        long? workflowBytes = null,
        long? logBytes = null,
        long? vectorBytes = null,
        long? tableBytes = null,
        long? blobStorageBytes = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var usage = await context.OrganizationUsages
            .FirstOrDefaultAsync(u => u.OrganizationId == orgId);
        
        if (usage == null) return;
        
        if (workflowBytes.HasValue)
            usage.StorageBytesWorkflows = Math.Max(0, usage.StorageBytesWorkflows + workflowBytes.Value);
        
        if (logBytes.HasValue)
            usage.StorageBytesLogs = Math.Max(0, usage.StorageBytesLogs + logBytes.Value);
        
        if (vectorBytes.HasValue)
            usage.StorageBytesVectors = Math.Max(0, usage.StorageBytesVectors + vectorBytes.Value);
        
        if (tableBytes.HasValue)
            usage.StorageBytesTables = Math.Max(0, usage.StorageBytesTables + tableBytes.Value);
        
        if (blobStorageBytes.HasValue)
            usage.StorageBytesBlobStorage = Math.Max(0, usage.StorageBytesBlobStorage + blobStorageBytes.Value);
        
        usage.LastUpdated = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Check if organization can create another workflow.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanCreateWorkflowAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var limits = await GetOrgLimitsAsync(orgId);
        
        if (limits.MaxWorkflows <= 0) // 0 = unlimited
            return (true, null);
        
        var currentCount = await context.Workflows
            .CountAsync(w => w.OrganizationId == orgId);
        
        if (currentCount >= limits.MaxWorkflows)
        {
            return (false, $"Organization has reached the maximum of {limits.MaxWorkflows} workflows");
        }
        
        return (true, null);
    }

    /// <summary>
    /// Check if a workflow can add another node.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanAddNodeAsync(Guid orgId, Guid workflowId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var limits = await GetOrgLimitsAsync(orgId);
        
        if (limits.MaxNodesPerWorkflow <= 0) // 0 = unlimited
            return (true, null);
        
        var currentCount = await context.WorkflowNodes
            .CountAsync(n => n.WorkflowId == workflowId);
        
        if (currentCount >= limits.MaxNodesPerWorkflow)
        {
            return (false, $"Workflow has reached the maximum of {limits.MaxNodesPerWorkflow} nodes");
        }
        
        return (true, null);
    }

    /// <summary>
    /// Check if organization can add more vector documents.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanAddVectorDocAsync(Guid orgId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var limits = await GetOrgLimitsAsync(orgId);
        
        if (limits.MaxVectorDocs <= 0) // 0 = unlimited
            return (true, null);
        
        // Count vector docs across all org workflows
        var orgWorkflowIds = await context.Workflows
            .Where(w => w.OrganizationId == orgId)
            .Select(w => w.Id)
            .ToListAsync();
        
        var orgNodeIds = await context.WorkflowNodes
            .Where(n => orgWorkflowIds.Contains(n.WorkflowId))
            .Select(n => n.Id)
            .ToListAsync();
        
        var currentCount = await context.VectorDocuments
            .CountAsync(v => orgNodeIds.Contains(v.VectorDbNodeId));
        
        if (currentCount >= limits.MaxVectorDocs)
        {
            return (false, $"Organization has reached the maximum of {limits.MaxVectorDocs} vector documents");
        }
        
        return (true, null);
    }

    #endregion

    /// <summary>
    /// Get current usage and limits for dashboard display.
    /// </summary>
    public async Task<OrganizationUsageSummary> GetUsageSummaryAsync(Guid orgId)
    {
        var usage = await EnsureUsageAsync(orgId);
        var limits = await GetOrgLimitsAsync(orgId);
        
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var workflowCount = await context.Workflows
            .CountAsync(w => w.OrganizationId == orgId);
        
        var memberCount = await context.OrganizationMembers
            .CountAsync(m => m.OrganizationId == orgId);
        
        return new OrganizationUsageSummary
        {
            // Counts
            WorkflowCount = workflowCount,
            MaxWorkflows = limits.MaxWorkflows,
            MemberCount = memberCount,
            MaxMembers = limits.MaxMembersPerOrganization,
            ExecutionsThisMonth = usage.ExecutionsThisMonth,
            MaxExecutionsPerMonth = limits.MaxExecutionsPerMonth,
            
            // Storage
            TotalStorageBytes = usage.TotalStorageBytes,
            MaxStorageBytes = limits.MaxStorageBytes,
            StorageBytesWorkflows = usage.StorageBytesWorkflows,
            StorageBytesLogs = usage.StorageBytesLogs,
            StorageBytesVectors = usage.StorageBytesVectors,
            StorageBytesTables = usage.StorageBytesTables,
            StorageBytesBlobStorage = usage.StorageBytesBlobStorage,
            
            // Period
            PeriodStart = usage.PeriodStart,
            LastUpdated = usage.LastUpdated
        };
    }
}

#region DTOs

public class OrganizationLimits
{
    public int MaxWorkflows { get; set; }
    public int MaxNodesPerWorkflow { get; set; }
    public int MaxExecutionsPerMonth { get; set; }
    public long MaxStorageBytes { get; set; }
    public int MaxVectorDocs { get; set; }
    public int MaxMembersPerOrganization { get; set; }
}

public class OrganizationUsageSummary
{
    public int WorkflowCount { get; set; }
    public int MaxWorkflows { get; set; }
    public int MemberCount { get; set; }
    public int MaxMembers { get; set; }
    public int ExecutionsThisMonth { get; set; }
    public int MaxExecutionsPerMonth { get; set; }
    
    public long TotalStorageBytes { get; set; }
    public long MaxStorageBytes { get; set; }
    public long StorageBytesWorkflows { get; set; }
    public long StorageBytesLogs { get; set; }
    public long StorageBytesVectors { get; set; }
    public long StorageBytesTables { get; set; }
    public long StorageBytesBlobStorage { get; set; }
    
    public DateTime PeriodStart { get; set; }
    public DateTime LastUpdated { get; set; }
    
    // Computed percentages
    public double WorkflowUsagePercent => MaxWorkflows > 0 ? (double)WorkflowCount / MaxWorkflows * 100 : 0;
    public double MemberUsagePercent => MaxMembers > 0 ? (double)MemberCount / MaxMembers * 100 : 0;
    public double ExecutionUsagePercent => MaxExecutionsPerMonth > 0 ? (double)ExecutionsThisMonth / MaxExecutionsPerMonth * 100 : 0;
    public double StorageUsagePercent => MaxStorageBytes > 0 ? (double)TotalStorageBytes / MaxStorageBytes * 100 : 0;
}

#endregion
