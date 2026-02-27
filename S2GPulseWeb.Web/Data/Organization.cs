namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Represents an organization that can contain multiple users and shared workflows.
/// Organizations provide isolated storage and quota pools separate from personal user accounts.
/// </summary>
public class Organization
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// Display name of the organization
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Optional description of the organization
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// User-uploaded SVG logo for the organization.
    /// Displayed in the navigation context switcher.
    /// </summary>
    public string? SvgLogo { get; set; }
    
    /// <summary>
    /// FK to the user who created this organization (immutable after creation).
    /// The Founder has full control including deletion rights.
    /// </summary>
    public string FounderId { get; set; } = string.Empty;
    
    /// <summary>
    /// Navigation property to the founding user
    /// </summary>
    public ApplicationUser Founder { get; set; } = null!;
    
    /// <summary>
    /// Timestamp when the organization was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Timestamp when the organization was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Soft delete flag. When false, the organization is considered deleted.
    /// </summary>
    public bool IsActive { get; set; } = true;
    
    /// <summary>
    /// Plan-gated disable flag. When true, the organization is disabled because the founder's
    /// membership plan no longer supports organizations. Distinct from IsActive (soft delete).
    /// Automatically re-enabled when the founder upgrades to an org-capable plan.
    /// </summary>
    public bool IsDisabled { get; set; } = false;
    
    /// <summary>
    /// Timestamp of when the organization was disabled. Used to calculate the 30-day grace period
    /// before automatic cleanup. Null when org is not disabled.
    /// </summary>
    public DateTime? DisabledAt { get; set; }
    
    /// <summary>
    /// Navigation property to organization members
    /// </summary>
    public ICollection<OrganizationMember> Members { get; set; } = new List<OrganizationMember>();
    
    /// <summary>
    /// Navigation property to workflows owned by this organization
    /// </summary>
    public ICollection<Workflow> Workflows { get; set; } = new List<Workflow>();
}
