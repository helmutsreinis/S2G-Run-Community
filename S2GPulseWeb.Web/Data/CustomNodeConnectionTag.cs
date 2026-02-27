namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Defines a connection tag (routing label) that can be triggered by a custom node's script
/// to control which downstream connections are activated.
/// </summary>
public class CustomNodeConnectionTag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid NodeDefinitionId { get; set; }
    public CustomNodeDefinition NodeDefinition { get; set; } = null!;
    
    /// <summary>Tag name used in connections (e.g., "success", "error", "retry")</summary>
    public string TagName { get; set; } = "";
    
    /// <summary>Optional description for documentation</summary>
    public string? Description { get; set; }
    
    /// <summary>CSS color for visual hint in connection lines (e.g., "#22c55e" for green)</summary>
    public string? Color { get; set; }
    
    /// <summary>Human-readable description of when this tag is triggered</summary>
    public string ConditionDescription { get; set; } = "";
    
    /// <summary>Order for display in documentation (lower = first)</summary>
    public int DisplayOrder { get; set; }
}
