namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Subscription tiers for S2G-Pulse users
/// </summary>
public enum SubscriptionTier
{
    /// <summary>
    /// Free tier - 500 executions/month, 100MB storage
    /// </summary>
    Free = 0,
    
    /// <summary>
    /// Starter tier ($12/mo) - 5,000 executions/month, 500MB storage
    /// </summary>
    Starter = 1
    
    // Future tiers:
    // Pro = 2,
    // Business = 3
}
