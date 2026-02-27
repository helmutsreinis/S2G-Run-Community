namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Defines the role a user has within an organization.
/// Roles determine permission levels for organization management.
/// </summary>
public enum OrganizationRole
{
    /// <summary>
    /// Can view and edit organization workflows.
    /// Cannot manage members or organization settings.
    /// </summary>
    Contributor = 0,
    
    /// <summary>
    /// Full management rights except deleting the organization.
    /// Can add/remove members, change roles, and manage all settings.
    /// </summary>
    Owner = 1,
    
    /// <summary>
    /// The user who created the organization.
    /// Has full control including the ability to delete the organization.
    /// This role is automatically assigned and cannot be transferred.
    /// </summary>
    Founder = 2
}

/// <summary>
/// Junction table representing a user's membership in an organization.
/// Tracks role, join date, and invitation metadata.
/// </summary>
public class OrganizationMember
{
    public int Id { get; set; }
    
    /// <summary>
    /// FK to the organization
    /// </summary>
    public Guid OrganizationId { get; set; }
    
    /// <summary>
    /// Navigation property to the organization
    /// </summary>
    public Organization Organization { get; set; } = null!;
    
    /// <summary>
    /// FK to the member user
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>
    /// Navigation property to the member user
    /// </summary>
    public ApplicationUser User { get; set; } = null!;
    
    /// <summary>
    /// The user's role within this organization
    /// </summary>
    public OrganizationRole Role { get; set; } = OrganizationRole.Contributor;
    
    /// <summary>
    /// Timestamp when the user joined/was added to the organization
    /// </summary>
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Timestamp when the invitation was sent (if applicable)
    /// </summary>
    public DateTime? InvitedAt { get; set; }
    
    /// <summary>
    /// User ID of the person who invited this member
    /// </summary>
    public string? InvitedByUserId { get; set; }
}
