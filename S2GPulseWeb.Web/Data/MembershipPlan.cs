namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Represents a configurable membership plan with quotas and feature flags
/// </summary>
public class MembershipPlan
{
    public int Id { get; set; }
    
    // Display
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? DetailedDescription { get; set; }
    public string? SvgIcon { get; set; }
    public string? BadgeColorGradientStart { get; set; }
    public string? BadgeColorGradientEnd { get; set; }
    public string? BadgeTextColor { get; set; }
    public string? BadgeBorderColor { get; set; }
    
    // Pricing
    public decimal MonthlyPrice { get; set; }
    public string? StripePriceId { get; set; }
    public bool IsFree { get; set; }
    public bool IsContactSales { get; set; }
    
    // Quotas
    public int MaxExecutionsPerMonth { get; set; }
    public long MaxStorageBytes { get; set; }
    public int MaxWorkflows { get; set; }
    public int MaxNodesPerWorkflow { get; set; }
    public int MaxVectorDocs { get; set; }
    public int LogRetentionHours { get; set; }
    
    // Feature Flags
    public bool CanImportExport { get; set; }
    public bool CanUseScheduling { get; set; }
    public int MaxHttpListeners { get; set; }
    
    // ============================================
    // Organization Feature Quotas
    // ============================================
    
    /// <summary>
    /// Maximum organizations the user can create (0 = feature disabled for this plan)
    /// </summary>
    public int MaxOrganizations { get; set; } = 0;
    
    /// <summary>
    /// Maximum members allowed per organization (0 = unlimited when feature enabled)
    /// </summary>
    public int MaxMembersPerOrganization { get; set; } = 0;
    
    /// <summary>
    /// Maximum workflows per organization (0 = unlimited when feature enabled)
    /// </summary>
    public int MaxWorkflowsPerOrganization { get; set; } = 0;
    
    /// <summary>
    /// Maximum nodes per workflow in organization context (0 = use personal limit)
    /// </summary>
    public int MaxNodesPerOrgWorkflow { get; set; } = 0;
    
    /// <summary>
    /// Maximum executions per month for organization workflows (0 = use personal limit)
    /// </summary>
    public int MaxOrgExecutionsPerMonth { get; set; } = 0;
    
    /// <summary>
    /// Maximum storage bytes for organization (0 = use personal limit)
    /// </summary>
    public long MaxOrgStorageBytes { get; set; } = 0;
    
    /// <summary>
    /// Maximum vector documents per organization (0 = use personal limit)
    /// </summary>
    public int MaxOrgVectorDocs { get; set; } = 0;
    
    // Capacity Management
    public int? MaxPaidMembers { get; set; }
    
    // Ordering & Status
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
