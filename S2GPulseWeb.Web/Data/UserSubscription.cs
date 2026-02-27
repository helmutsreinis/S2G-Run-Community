namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Entity tracking user subscription state with Stripe integration
/// </summary>
public class UserSubscription
{
    public int Id { get; set; }
    
    /// <summary>
    /// FK to ApplicationUser.Id
    /// </summary>
    public string UserId { get; set; } = null!;
    
    /// <summary>
    /// Navigation property to user
    /// </summary>
    public ApplicationUser User { get; set; } = null!;
    
    /// <summary>
    /// FK to MembershipPlan (new dynamic plan system)
    /// </summary>
    public int? MembershipPlanId { get; set; }
    
    /// <summary>
    /// Navigation property to membership plan
    /// </summary>
    public MembershipPlan? MembershipPlan { get; set; }
    
    /// <summary>
    /// Current subscription tier (deprecated - use MembershipPlanId)
    /// </summary>
    [Obsolete("Use MembershipPlanId instead. Kept for migration compatibility.")]
    public SubscriptionTier Tier { get; set; } = SubscriptionTier.Free;
    
    /// <summary>
    /// Stripe Customer ID (cus_xxxxx)
    /// </summary>
    public string? StripeCustomerId { get; set; }
    
    /// <summary>
    /// Stripe Subscription ID (sub_xxxxx)
    /// </summary>
    public string? StripeSubscriptionId { get; set; }
    
    /// <summary>
    /// Stripe Price ID for current subscription
    /// </summary>
    public string? StripePriceId { get; set; }
    
    /// <summary>
    /// Current billing period start
    /// </summary>
    public DateTime? CurrentPeriodStart { get; set; }
    
    /// <summary>
    /// Current billing period end
    /// </summary>
    public DateTime? CurrentPeriodEnd { get; set; }
    
    /// <summary>
    /// Whether the subscription will cancel at period end (user chose not to renew)
    /// </summary>
    public bool CancelAtPeriodEnd { get; set; } = false;
    
    /// <summary>
    /// Subscription status: free, active, canceled, past_due, incomplete
    /// </summary>
    public string Status { get; set; } = "free";
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
