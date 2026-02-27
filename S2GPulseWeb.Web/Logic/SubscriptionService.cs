using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing user subscriptions
/// </summary>
public class SubscriptionService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IConfiguration configuration,
        ILogger<SubscriptionService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _configuration = configuration;
        _logger = logger;
        _isSelfHosted = configuration.GetValue<bool>("SelfHosted");
    }
    
    private readonly bool _isSelfHosted;
    
    /// <summary>
    /// Get user's current membership plan
    /// </summary>
    public async Task<MembershipPlan?> GetUserPlanAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var subscription = await db.UserSubscriptions
            .Include(s => s.MembershipPlan)
            .FirstOrDefaultAsync(s => s.UserId == userId);
        return subscription?.MembershipPlan;
    }

    /// <summary>
    /// Get user's subscription, creating a Free tier subscription if none exists
    /// </summary>
    public async Task<UserSubscription> EnsureSubscriptionAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var subscription = await db.UserSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId);
            
        if (subscription == null)
        {
            // In self-hosted mode, assign the Self-Hosted (unlimited) plan
            int? planId = null;
            if (_isSelfHosted)
            {
                var selfHostedPlan = await db.MembershipPlans
                    .FirstOrDefaultAsync(p => p.IsFree && p.IsActive);
                planId = selfHostedPlan?.Id;
            }
            
            subscription = new UserSubscription
            {
                UserId = userId,
                MembershipPlanId = planId,
                #pragma warning disable CS0618 // Using obsolete Tier for backward compatibility
                Tier = SubscriptionTier.Free,
                #pragma warning restore CS0618
                Status = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            
            db.UserSubscriptions.Add(subscription);
            await db.SaveChangesAsync();
            
            _logger.LogInformation(_isSelfHosted 
                ? "Created Self-Hosted subscription for user {UserId}" 
                : "Created Free subscription for user {UserId}", userId);
        }
        
        return subscription;
    }

    /// <summary>
    /// Get user's current subscription
    /// </summary>
    public async Task<UserSubscription?> GetSubscriptionAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.UserSubscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
    }

    /// <summary>
    /// Get user's current tier, defaults to Free
    /// </summary>
    public async Task<SubscriptionTier> GetTierAsync(string userId)
    {
        var subscription = await GetSubscriptionAsync(userId);
        #pragma warning disable CS0618 // Using obsolete Tier for backward compatibility
        return subscription?.Tier ?? SubscriptionTier.Free;
        #pragma warning restore CS0618
    }

    /// <summary>
    /// Update subscription from Stripe webhook data
    /// </summary>
    public async Task UpdateSubscriptionFromStripeAsync(
        string stripeCustomerId,
        string stripeSubscriptionId,
        string stripePriceId,
        string status,
        DateTime? periodStart,
        DateTime? periodEnd,
        bool cancelAtPeriodEnd = false)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var subscription = await db.UserSubscriptions
            .FirstOrDefaultAsync(s => s.StripeCustomerId == stripeCustomerId);
            
        if (subscription == null)
        {
            _logger.LogWarning("No subscription found for Stripe customer {CustomerId}", stripeCustomerId);
            return;
        }

        // Look up MembershipPlan by StripePriceId
        MembershipPlan? plan = null;
        if (!string.IsNullOrEmpty(stripePriceId))
        {
            plan = await db.MembershipPlans.FirstOrDefaultAsync(p => p.StripePriceId == stripePriceId);
        }
        
        // If no plan found and status is canceled, find free plan
        if (plan == null && (status == "canceled" || string.IsNullOrEmpty(stripePriceId)))
        {
            plan = await db.MembershipPlans.FirstOrDefaultAsync(p => p.IsFree && p.IsActive);
        }

        #pragma warning disable CS0618 // Using obsolete Tier property for backward compatibility
        // Map Stripe status to tier - keep Starter if active (even if canceling)
        var tier = (status == "active" || status == "trialing") ? SubscriptionTier.Starter : SubscriptionTier.Free;
        subscription.Tier = tier;
        #pragma warning restore CS0618
        
        subscription.StripeSubscriptionId = stripeSubscriptionId;
        subscription.StripePriceId = stripePriceId;
        subscription.MembershipPlanId = plan?.Id;
        subscription.Status = status;
        subscription.CurrentPeriodStart = periodStart;
        subscription.CurrentPeriodEnd = periodEnd;
        subscription.CancelAtPeriodEnd = cancelAtPeriodEnd;
        subscription.UpdatedAt = DateTime.UtcNow;
        
        await db.SaveChangesAsync();
        
        _logger.LogInformation(
            "Updated subscription for customer {CustomerId}: PlanId={PlanId}, Status={Status}, CancelAtPeriodEnd={CancelAtPeriodEnd}", 
            stripeCustomerId, plan?.Id, status, cancelAtPeriodEnd);
    }

    /// <summary>
    /// Link a Stripe customer ID to a user subscription
    /// </summary>
    public async Task SetStripeCustomerIdAsync(string userId, string stripeCustomerId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var subscription = await db.UserSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId);
            
        if (subscription != null)
        {
            subscription.StripeCustomerId = stripeCustomerId;
            subscription.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Check if user is the master admin.
    /// In self-hosted mode: first registered user becomes admin if MasterUserEmail is not set.
    /// </summary>
    public async Task<bool> IsAdminAsync(string userId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        
        if (user == null) return false;
        
        var masterEmail = _configuration["Admin:MasterUserEmail"];
        
        // If MasterUserEmail is set, use it (works in both cloud and self-hosted)
        if (!string.IsNullOrEmpty(masterEmail))
        {
            return string.Equals(user.Email, masterEmail, StringComparison.OrdinalIgnoreCase);
        }
        
        // Self-hosted mode with no MasterUserEmail: first registered user is admin
        if (_isSelfHosted)
        {
            var firstUser = await db.Users
                .OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync();
            return firstUser?.Id == userId;
        }
        
        return false;
    }

    /// <summary>
    /// Get all subscriptions for admin view. Creates Free tier subscriptions for any users who don't have one.
    /// </summary>
    public async Task<List<UserSubscription>> GetAllSubscriptionsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        // Find all users without subscriptions and create Free tier subscriptions for them
        var usersWithoutSubs = await db.Users
            .Where(u => !db.UserSubscriptions.Any(s => s.UserId == u.Id))
            .ToListAsync();
        
        if (usersWithoutSubs.Any())
        {
            foreach (var user in usersWithoutSubs)
            {
                db.UserSubscriptions.Add(new UserSubscription
                {
                    UserId = user.Id,
                    #pragma warning disable CS0618 // Using obsolete Tier for backward compatibility
                    Tier = SubscriptionTier.Free,
                    #pragma warning restore CS0618
                    Status = "free",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
            _logger.LogInformation("Created Free subscriptions for {Count} users without subscriptions", usersWithoutSubs.Count);
        }
        
        return await db.UserSubscriptions
            .Include(s => s.User)
            .Include(s => s.MembershipPlan)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Admin: Manually set a user's tier
    /// </summary>
    [Obsolete("Use SetPlanAsync instead")]
    public async Task SetTierAsync(string userId, SubscriptionTier tier)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var subscription = await db.UserSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId);
            
        if (subscription != null)
        {
            #pragma warning disable CS0618
            subscription.Tier = tier;
            #pragma warning restore CS0618
            subscription.Status = tier == SubscriptionTier.Free ? "free" : "active";
            subscription.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            
            _logger.LogInformation("Admin set tier for user {UserId} to {Tier}", userId, tier);
        }
    }
    
    /// <summary>
    /// Admin: Manually set a user's membership plan
    /// </summary>
    public async Task SetPlanAsync(string userId, int planId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var subscription = await db.UserSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId);
        
        var plan = await db.MembershipPlans.FindAsync(planId);
        if (subscription == null || plan == null) return;
        
        subscription.MembershipPlanId = planId;
        #pragma warning disable CS0618
        subscription.Tier = plan.IsFree ? SubscriptionTier.Free : SubscriptionTier.Starter;
        #pragma warning restore CS0618
        subscription.Status = plan.IsFree ? "free" : "active";
        subscription.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Admin set plan for user {UserId} to {PlanName} (ID: {PlanId})", userId, plan.Name, planId);
        
        // Handle downgrade/upgrade side-effects
        if (plan.IsFree)
        {
            await HandleDowngradeAsync(userId, plan);
        }
        else
        {
            await HandleUpgradeAsync(userId, plan);
        }
    }
    
    /// <summary>
    /// Handle downgrade side-effects: reset personal usage, disable orgs if plan doesn't support them,
    /// and clear active org context if it's now disabled.
    /// </summary>
    public async Task HandleDowngradeAsync(string userId, MembershipPlan newPlan)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        // 1. Reset personal usage metrics
        var usage = await db.UserUsages.FirstOrDefaultAsync(u => u.UserId == userId);
        if (usage != null)
        {
            usage.ExecutionsThisMonth = 0;
            usage.PeriodStart = DateTime.UtcNow;
            usage.LastUpdated = DateTime.UtcNow;
            _logger.LogInformation("Reset personal usage metrics for user {UserId} on downgrade", userId);
        }
        
        // 2. Disable organizations if new plan doesn't support them
        if (newPlan.MaxOrganizations <= 0)
        {
            var founderOrgs = await db.Organizations
                .Where(o => o.FounderId == userId && o.IsActive && !o.IsDisabled)
                .ToListAsync();
            
            foreach (var org in founderOrgs)
            {
                org.IsDisabled = true;
                org.DisabledAt = DateTime.UtcNow;
                org.UpdatedAt = DateTime.UtcNow;
                _logger.LogInformation("Disabled organization {OrgId} ({OrgName}) due to user {UserId} downgrade", 
                    org.Id, org.Name, userId);
            }
            
            // 3. Clear active org context if it's now disabled
            if (founderOrgs.Any())
            {
                var pref = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
                if (pref?.ActiveOrganizationId != null)
                {
                    var activeOrgDisabled = founderOrgs.Any(o => o.Id == pref.ActiveOrganizationId.Value);
                    if (activeOrgDisabled)
                    {
                        pref.ActiveOrganizationId = null;
                        pref.UpdatedAt = DateTime.UtcNow;
                        _logger.LogInformation("Cleared active organization context for user {UserId}", userId);
                    }
                }
                
                // Also clear active org for any member who has a disabled org selected
                var disabledOrgIds = founderOrgs.Select(o => o.Id).ToList();
                var affectedPrefs = await db.UserPreferences
                    .Where(p => p.ActiveOrganizationId != null && disabledOrgIds.Contains(p.ActiveOrganizationId.Value))
                    .ToListAsync();
                
                foreach (var affectedPref in affectedPrefs)
                {
                    affectedPref.ActiveOrganizationId = null;
                    affectedPref.UpdatedAt = DateTime.UtcNow;
                }
            }
        }
        
        await db.SaveChangesAsync();
    }
    
    /// <summary>
    /// Handle upgrade side-effects: re-enable previously disabled organizations if plan supports them.
    /// </summary>
    public async Task HandleUpgradeAsync(string userId, MembershipPlan newPlan)
    {
        if (newPlan.MaxOrganizations <= 0) return;
        
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var disabledOrgs = await db.Organizations
            .Where(o => o.FounderId == userId && o.IsActive && o.IsDisabled)
            .ToListAsync();
        
        foreach (var org in disabledOrgs)
        {
            org.IsDisabled = false;
            org.DisabledAt = null;
            org.UpdatedAt = DateTime.UtcNow;
            _logger.LogInformation("Re-enabled organization {OrgId} ({OrgName}) due to user {UserId} upgrade",
                org.Id, org.Name, userId);
        }
        
        if (disabledOrgs.Any())
        {
            await db.SaveChangesAsync();
        }
    }
}
