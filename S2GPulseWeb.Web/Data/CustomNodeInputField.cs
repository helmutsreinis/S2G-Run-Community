namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Defines a configurable input field shown in the node editor for custom nodes.
/// </summary>
public class CustomNodeInputField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid NodeDefinitionId { get; set; }
    public CustomNodeDefinition NodeDefinition { get; set; } = null!;
    
    /// <summary>Field identifier used in scripts (e.g., "apiKey")</summary>
    public string FieldName { get; set; } = "";
    
    /// <summary>Display label shown in UI (e.g., "API Key")</summary>
    public string DisplayLabel { get; set; } = "";
    
    /// <summary>Placeholder text for the input</summary>
    public string? PlaceholderText { get; set; }
    
    /// <summary>Help text shown below the input</summary>
    public string? HelpText { get; set; }
    
    /// <summary>Type of input control to render</summary>
    public CustomFieldType FieldType { get; set; } = CustomFieldType.Text;
    
    /// <summary>Default value for new node instances</summary>
    public string? DefaultValue { get; set; }
    
    /// <summary>Whether this field is required</summary>
    public bool IsRequired { get; set; } = false;
    
    /// <summary>Whether the field supports {{placeholder}} syntax</summary>
    public bool AllowPlaceholders { get; set; } = true;
    
    // Validation
    /// <summary>Optional regex pattern for validation</summary>
    public string? ValidationRegex { get; set; }
    
    /// <summary>Expected data type: "string", "int", "decimal", "bool", "json"</summary>
    public string? ExpectedDataType { get; set; }
    
    /// <summary>JSON Schema for validation when ExpectedDataType is "json"</summary>
    public string? JsonSchemaValidation { get; set; }
    
    /// <summary>For Select type: JSON array of options (e.g., ["Option1", "Option2"])</summary>
    public string? SelectOptionsJson { get; set; }
    
    /// <summary>Order for display in the editor (lower = first)</summary>
    public int DisplayOrder { get; set; }
}

/// <summary>
/// Types of input controls available for custom node fields.
/// </summary>
public enum CustomFieldType
{
    /// <summary>Single-line text input</summary>
    Text,
    
    /// <summary>Multi-line text area</summary>
    TextArea,
    
    /// <summary>Numeric input</summary>
    Number,
    
    /// <summary>Checkbox/toggle</summary>
    Boolean,
    
    /// <summary>Dropdown select (options from SelectOptionsJson)</summary>
    Select,
    
    /// <summary>Password input (masked)</summary>
    Password,
    
    /// <summary>JSON editor with validation</summary>
    Json,
    
    /// <summary>Code editor with syntax highlighting</summary>
    Code
}
