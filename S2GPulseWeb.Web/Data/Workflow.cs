namespace S2GPulseWeb.Web.Data;

public class Workflow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public ApplicationUser Owner { get; set; } = null!;
    
    /// <summary>
    /// Organization ownership (null = personal workflow).
    /// When set, workflow belongs to the organization and is visible to all members.
    /// </summary>
    public Guid? OrganizationId { get; set; }
    
    /// <summary>
    /// Navigation property to the owning organization
    /// </summary>
    public Organization? Organization { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = false;
    
    public ICollection<WorkflowNode> Nodes { get; set; } = new List<WorkflowNode>();
}
