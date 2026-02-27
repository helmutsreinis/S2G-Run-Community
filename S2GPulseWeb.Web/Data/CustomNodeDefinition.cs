namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Core entity storing a complete custom node definition created via the Node Designer.
/// </summary>
public class CustomNodeDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // Identity
    /// <summary>Unique key used as NodeType (e.g., "Custom_JsonTransformer")</summary>
    public string NodeTypeKey { get; set; } = "";
    
    /// <summary>Display name shown in UI (e.g., "JSON Transformer")</summary>
    public string DisplayName { get; set; } = "";
    
    /// <summary>Optional description for documentation</summary>
    public string? Description { get; set; }
    
    // Icon - Raw SVG code provided by admin
    /// <summary>Raw SVG markup for the node icon (e.g., "&lt;svg&gt;...&lt;/svg&gt;")</summary>
    public string IconSvg { get; set; } = "";
    
    /// <summary>Optional emoji fallback for minimal UI contexts where SVG cannot render</summary>
    public string? IconFallbackEmoji { get; set; }
    
    // Category
    public Guid? CategoryId { get; set; }
    public CustomNodeCategory? Category { get; set; }
    
    // Node Type Configuration
    /// <summary>Execution behavior type</summary>
    public CustomNodeExecutionType ExecutionType { get; set; } = CustomNodeExecutionType.DataTransformation;
    
    /// <summary>Optional delay in milliseconds before execution</summary>
    public int ExecutionDelayMs { get; set; } = 0;
    
    /// <summary>Timeout for script execution in seconds</summary>
    public int TimeoutSeconds { get; set; } = 30;
    
    // JavaScript Logic
    /// <summary>Main Jint JavaScript code to execute</summary>
    public string Script { get; set; } = "";
    
    /// <summary>Optional initialization script run before main execution</summary>
    public string? InitializationScript { get; set; }
    
    // Schema & Validation
    /// <summary>Optional JSON Schema for overall input validation</summary>
    public string? InputSchemaJson { get; set; }
    
    // Default Configuration
    /// <summary>Default JSON configuration for new instances of this node type</summary>
    public string? DefaultConfigurationJson { get; set; }
    
    // Metadata
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Whether this node type is available in the designer</summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>Version number incremented on each update</summary>
    public int Version { get; set; } = 1;
    
    // Navigation
    public ICollection<CustomNodeInputField> InputFields { get; set; } = new List<CustomNodeInputField>();
    public ICollection<CustomNodeOutputParameter> OutputParameters { get; set; } = new List<CustomNodeOutputParameter>();
    public ICollection<CustomNodeConnectionTag> ConnectionTags { get; set; } = new List<CustomNodeConnectionTag>();
    public ICollection<CustomNodeLogConfig> LogConfigs { get; set; } = new List<CustomNodeLogConfig>();
}

/// <summary>
/// Defines the execution behavior pattern for custom nodes.
/// </summary>
public enum CustomNodeExecutionType
{
    /// <summary>Standard synchronous data processing</summary>
    DataTransformation,
    
    /// <summary>Allows HTTP client usage within scripts</summary>
    HttpRequest,
    
    /// <summary>Streaming HTTP response handling</summary>
    HttpStream,
    
    /// <summary>Can trigger downstream execution asynchronously</summary>
    Trigger,
    
    /// <summary>Batching/aggregation behavior</summary>
    Aggregator
}
