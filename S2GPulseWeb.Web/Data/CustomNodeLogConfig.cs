namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Configures logging behavior for specific inputs, outputs, or variables in a custom node.
/// </summary>
public class CustomNodeLogConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid NodeDefinitionId { get; set; }
    public CustomNodeDefinition NodeDefinition { get; set; } = null!;
    
    /// <summary>What type of value to log</summary>
    public CustomLogTarget LogTarget { get; set; }
    
    /// <summary>Name of the field, parameter, or variable to log</summary>
    public string TargetName { get; set; } = "";
    
    /// <summary>Log severity level</summary>
    public NodeLogLevel LogLevel { get; set; } = NodeLogLevel.Info;
    
    /// <summary>Whether this logging rule is active</summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>Optional custom message format (supports {value} placeholder)</summary>
    public string? MessageFormat { get; set; }
}

/// <summary>
/// Types of values that can be logged by custom nodes.
/// </summary>
public enum CustomLogTarget
{
    /// <summary>Log an input field value when received</summary>
    Input,
    
    /// <summary>Log an output parameter value when set</summary>
    Output,
    
    /// <summary>Log a script variable (via log() call in JS)</summary>
    Variable
}
