namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Defines an output parameter exposed by a custom node for use as placeholders in downstream nodes.
/// </summary>
public class CustomNodeOutputParameter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid NodeDefinitionId { get; set; }
    public CustomNodeDefinition NodeDefinition { get; set; } = null!;
    
    /// <summary>Parameter name used in placeholders (e.g., "TransformedData" -> {{NodeName.TransformedData}})</summary>
    public string ParameterName { get; set; } = "";
    
    /// <summary>Optional description for documentation</summary>
    public string? Description { get; set; }
    
    /// <summary>Expected data type: "string", "int", "bool", "object", "array"</summary>
    public string DataType { get; set; } = "string";
    
    /// <summary>Order for display in documentation (lower = first)</summary>
    public int DisplayOrder { get; set; }
}
