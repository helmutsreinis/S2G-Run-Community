using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing membership plans
/// </summary>
public class MembershipPlanService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<MembershipPlanService> _logger;
    private readonly bool _isSelfHosted;

    public MembershipPlanService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<MembershipPlanService> logger,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _isSelfHosted = configuration.GetValue<bool>("SelfHosted");
    }

    /// <summary>
    /// Get all active plans ordered by display order
    /// </summary>
    public async Task<List<MembershipPlan>> GetAllPlansAsync(bool includeInactive = false)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var query = db.MembershipPlans.AsQueryable();
        
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }
        
        return await query.OrderBy(p => p.DisplayOrder).ToListAsync();
    }

    /// <summary>
    /// Get a plan by ID
    /// </summary>
    public async Task<MembershipPlan?> GetPlanByIdAsync(int id)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.MembershipPlans.FindAsync(id);
    }

    /// <summary>
    /// Get a plan by Stripe Price ID (for webhook processing)
    /// </summary>
    public async Task<MembershipPlan?> GetPlanByPriceIdAsync(string stripePriceId)
    {
        if (string.IsNullOrEmpty(stripePriceId)) return null;
        
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.MembershipPlans
            .FirstOrDefaultAsync(p => p.StripePriceId == stripePriceId);
    }

    /// <summary>
    /// Get the designated free plan
    /// </summary>
    public async Task<MembershipPlan?> GetFreePlanAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.MembershipPlans
            .Where(p => p.IsFree && p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Create a new membership plan
    /// </summary>
    public async Task<MembershipPlan> CreatePlanAsync(MembershipPlan plan)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        plan.CreatedAt = DateTime.UtcNow;
        plan.UpdatedAt = DateTime.UtcNow;
        
        db.MembershipPlans.Add(plan);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Created membership plan: {PlanName} (ID: {PlanId})", plan.Name, plan.Id);
        return plan;
    }

    /// <summary>
    /// Update an existing membership plan
    /// </summary>
    public async Task<MembershipPlan?> UpdatePlanAsync(MembershipPlan plan)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var existing = await db.MembershipPlans.FindAsync(plan.Id);
        if (existing == null) return null;
        
        // Update all properties
        existing.Name = plan.Name;
        existing.Description = plan.Description;
        existing.DetailedDescription = plan.DetailedDescription;
        existing.SvgIcon = plan.SvgIcon;
        existing.BadgeColorGradientStart = plan.BadgeColorGradientStart;
        existing.BadgeColorGradientEnd = plan.BadgeColorGradientEnd;
        existing.BadgeTextColor = plan.BadgeTextColor;
        existing.BadgeBorderColor = plan.BadgeBorderColor;
        existing.MonthlyPrice = plan.MonthlyPrice;
        existing.StripePriceId = plan.StripePriceId;
        existing.IsFree = plan.IsFree;
        existing.IsContactSales = plan.IsContactSales;
        existing.MaxExecutionsPerMonth = plan.MaxExecutionsPerMonth;
        existing.MaxStorageBytes = plan.MaxStorageBytes;
        existing.MaxWorkflows = plan.MaxWorkflows;
        existing.MaxNodesPerWorkflow = plan.MaxNodesPerWorkflow;
        existing.MaxVectorDocs = plan.MaxVectorDocs;
        existing.LogRetentionHours = plan.LogRetentionHours;
        existing.CanImportExport = plan.CanImportExport;
        existing.CanUseScheduling = plan.CanUseScheduling;
        existing.MaxHttpListeners = plan.MaxHttpListeners;
        existing.MaxPaidMembers = plan.MaxPaidMembers;
        existing.DisplayOrder = plan.DisplayOrder;
        existing.IsActive = plan.IsActive;
        
        // Organization quotas
        existing.MaxOrganizations = plan.MaxOrganizations;
        existing.MaxMembersPerOrganization = plan.MaxMembersPerOrganization;
        existing.MaxWorkflowsPerOrganization = plan.MaxWorkflowsPerOrganization;
        existing.MaxNodesPerOrgWorkflow = plan.MaxNodesPerOrgWorkflow;
        existing.MaxOrgExecutionsPerMonth = plan.MaxOrgExecutionsPerMonth;
        existing.MaxOrgStorageBytes = plan.MaxOrgStorageBytes;
        existing.MaxOrgVectorDocs = plan.MaxOrgVectorDocs;
        
        existing.UpdatedAt = DateTime.UtcNow;
        
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Updated membership plan: {PlanName} (ID: {PlanId})", existing.Name, existing.Id);
        return existing;
    }

    /// <summary>
    /// Soft delete a plan (sets IsActive = false)
    /// </summary>
    public async Task<bool> DeletePlanAsync(int id)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var plan = await db.MembershipPlans.FindAsync(id);
        if (plan == null) return false;
        
        plan.IsActive = false;
        plan.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Soft deleted membership plan: {PlanName} (ID: {PlanId})", plan.Name, plan.Id);
        return true;
    }

    /// <summary>
    /// Get the count of active subscribers for a plan
    /// </summary>
    public async Task<int> GetCurrentMemberCountAsync(int planId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        // Count users on this plan with active or free status
        return await db.UserSubscriptions
            .CountAsync(s => s.MembershipPlanId == planId && (s.Status == "active" || s.Status == "free"));
    }

    /// <summary>
    /// Check if a plan can accept new members based on MaxPaidMembers
    /// </summary>
    public async Task<(bool CanAccept, string? Reason)> CanAcceptNewMembersAsync(int planId)
    {
        var plan = await GetPlanByIdAsync(planId);
        if (plan == null)
        {
            return (false, "Plan not found");
        }
        
        if (!plan.IsActive)
        {
            return (false, "This plan is no longer available");
        }
        
        if (plan.MaxPaidMembers == null)
        {
            return (true, null); // Unlimited
        }
        
        var currentCount = await GetCurrentMemberCountAsync(planId);
        if (currentCount >= plan.MaxPaidMembers.Value)
        {
            return (false, $"This plan has reached its member capacity ({plan.MaxPaidMembers.Value} members)");
        }
        
        return (true, null);
    }

    /// <summary>
    /// Seed default plans if none exist (migration from hardcoded tiers)
    /// </summary>
    public async Task SeedDefaultPlansAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        if (await db.MembershipPlans.AnyAsync())
        {
            _logger.LogInformation("Membership plans already exist, skipping seed");
            return;
        }
        
        // Self-hosted mode: seed a single unlimited plan
        if (_isSelfHosted)
        {
            var selfHostedPlan = new MembershipPlan
            {
                Name = "Self-Hosted",
                Description = "Community Edition — all features unlocked",
                IsFree = true,
                MonthlyPrice = 0,
                MaxExecutionsPerMonth = -1,
                MaxStorageBytes = -1,
                MaxWorkflows = -1,
                MaxNodesPerWorkflow = -1,
                MaxVectorDocs = -1,
                LogRetentionHours = -1,
                CanImportExport = true,
                CanUseScheduling = true,
                MaxHttpListeners = -1,
                MaxOrganizations = -1,
                MaxMembersPerOrganization = -1,
                MaxWorkflowsPerOrganization = -1,
                MaxNodesPerOrgWorkflow = -1,
                MaxOrgExecutionsPerMonth = -1,
                MaxOrgStorageBytes = -1,
                MaxOrgVectorDocs = -1,
                DisplayOrder = 0,
                BadgeColorGradientStart = "#38a169",
                BadgeColorGradientEnd = "#276749",
                BadgeTextColor = "#c6f6d5",
                BadgeBorderColor = "#48bb78"
            };
            
            db.MembershipPlans.Add(selfHostedPlan);
            await db.SaveChangesAsync();
            
            _logger.LogInformation("Seeded Self-Hosted (Community Edition) plan with unlimited quotas");
            return;
        }
        
        var freePlan = new MembershipPlan
        {
            Name = "Free",
            Description = "Get started with automation basics",
            IsFree = true,
            MonthlyPrice = 0,
            MaxExecutionsPerMonth = 2_000,
            MaxStorageBytes = 50 * 1024 * 1024, // 50MB
            MaxWorkflows = 1,
            MaxNodesPerWorkflow = 6,
            MaxVectorDocs = 1000,
            LogRetentionHours = 4,
            CanImportExport = false,
            CanUseScheduling = false,
            MaxHttpListeners = 1,
            DisplayOrder = 0,
            BadgeColorGradientStart = "#4a5568",
            BadgeColorGradientEnd = "#2d3748",
            BadgeTextColor = "#a0aec0",
            BadgeBorderColor = "#4a5568"
        };
        
        var starterPlan = new MembershipPlan
        {
            Name = "Starter",
            Description = "Perfect for individuals getting started with automation",
            IsFree = false,
            MonthlyPrice = 12,
            MaxExecutionsPerMonth = 2_000_000,
            MaxStorageBytes = 500 * 1024 * 1024, // 500MB
            MaxWorkflows = 10,
            MaxNodesPerWorkflow = 50,
            MaxVectorDocs = 10000,
            LogRetentionHours = 168, // 7 days
            CanImportExport = true,
            CanUseScheduling = true,
            MaxHttpListeners = 5,
            DisplayOrder = 1,
            BadgeColorGradientStart = "#38a169",
            BadgeColorGradientEnd = "#276749",
            BadgeTextColor = "#c6f6d5",
            BadgeBorderColor = "#48bb78"
        };
        
        var proPlan = new MembershipPlan
        {
            Name = "Pro",
            Description = "For power users and small teams",
            IsFree = false,
            MonthlyPrice = 39,
            MaxExecutionsPerMonth = 25_000_000,
            MaxStorageBytes = 2L * 1024 * 1024 * 1024, // 2GB
            MaxWorkflows = 50,
            MaxNodesPerWorkflow = 100,
            MaxVectorDocs = 50000,
            LogRetentionHours = 720, // 30 days
            CanImportExport = true,
            CanUseScheduling = true,
            MaxHttpListeners = 20,
            DisplayOrder = 2,
            BadgeColorGradientStart = "#4299e1",
            BadgeColorGradientEnd = "#2b6cb0",
            BadgeTextColor = "#bee3f8",
            BadgeBorderColor = "#63b3ed"
        };
        
        var businessPlan = new MembershipPlan
        {
            Name = "Business",
            Description = "For teams needing collaboration and scale",
            IsFree = false,
            MonthlyPrice = 99,
            MaxExecutionsPerMonth = 100_000_000,
            MaxStorageBytes = 10L * 1024 * 1024 * 1024, // 10GB
            MaxWorkflows = -1, // Unlimited (use -1)
            MaxNodesPerWorkflow = -1, // Unlimited
            MaxVectorDocs = 200000,
            LogRetentionHours = 2160, // 90 days
            CanImportExport = true,
            CanUseScheduling = true,
            MaxHttpListeners = -1, // Unlimited
            DisplayOrder = 3,
            BadgeColorGradientStart = "#9f7aea",
            BadgeColorGradientEnd = "#6b46c1",
            BadgeTextColor = "#e9d8fd",
            BadgeBorderColor = "#b794f4"
        };
        
        var enterprisePlan = new MembershipPlan
        {
            Name = "Enterprise",
            Description = "Custom solutions for large organizations",
            IsFree = false,
            IsContactSales = true,
            MonthlyPrice = 0, // Contact sales
            MaxExecutionsPerMonth = -1, // Custom
            MaxStorageBytes = -1, // Custom
            MaxWorkflows = -1,
            MaxNodesPerWorkflow = -1,
            MaxVectorDocs = -1,
            LogRetentionHours = -1, // Custom
            CanImportExport = true,
            CanUseScheduling = true,
            MaxHttpListeners = -1,
            DisplayOrder = 4,
            BadgeColorGradientStart = "#ed8936",
            BadgeColorGradientEnd = "#c05621",
            BadgeTextColor = "#feebc8",
            BadgeBorderColor = "#ed8936"
        };
        
        db.MembershipPlans.AddRange(freePlan, starterPlan, proPlan, businessPlan, enterprisePlan);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Seeded {Count} default membership plans", 5);
        
        // Migrate existing users based on their tier
        await MigrateExistingUsersAsync(db, freePlan.Id, starterPlan.Id);
    }
    
    private async Task MigrateExistingUsersAsync(ApplicationDbContext db, int freePlanId, int starterPlanId)
    {
        #pragma warning disable CS0618 // Using obsolete Tier property for migration
        var subscriptions = await db.UserSubscriptions.ToListAsync();
        
        foreach (var sub in subscriptions)
        {
            if (sub.MembershipPlanId == null)
            {
                sub.MembershipPlanId = sub.Tier == SubscriptionTier.Free ? freePlanId : starterPlanId;
                sub.UpdatedAt = DateTime.UtcNow;
            }
        }
        
        await db.SaveChangesAsync();
        _logger.LogInformation("Migrated {Count} existing subscriptions to new plan system", subscriptions.Count);
        #pragma warning restore CS0618
    }
}
