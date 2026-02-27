using System.Text;
using System.Text.Json;
using S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service that compiles comprehensive node documentation for AI context.
/// Aggregates built-in nodes and custom nodes from JSON definitions.
/// </summary>
public class NodeKnowledgeService
{
    private readonly ILogger<NodeKnowledgeService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly BuiltInNodeCatalogService _builtInService;
    
    // Cache for compiled knowledge
    private string? _cachedKnowledge;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(1);
    private static readonly object _cacheLock = new();
    
    public NodeKnowledgeService(
        ILogger<NodeKnowledgeService> logger,
        IWebHostEnvironment env,
        BuiltInNodeCatalogService builtInService)
    {
        _logger = logger;
        _env = env;
        _builtInService = builtInService;
    }
    
    /// <summary>
    /// Gets the full node catalog documentation for AI context.
    /// Returns cached content if available and not expired.
    /// </summary>
    public string GetFullNodeCatalogDocumentation()
    {
        lock (_cacheLock)
        {
            if (_cachedKnowledge != null && DateTime.UtcNow - _lastCacheTime < CacheExpiration)
            {
                return _cachedKnowledge;
            }
            
            _cachedKnowledge = BuildNodeKnowledge();
            _lastCacheTime = DateTime.UtcNow;
            return _cachedKnowledge;
        }
    }
    
    /// <summary>
    /// Gets documentation for a specific node type.
    /// </summary>
    public string? GetNodeDocumentation(string nodeType)
    {
        // Check built-in nodes
        var builtIn = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes()
            .FirstOrDefault(n => n.NodeTypeKey.Equals(nodeType, StringComparison.OrdinalIgnoreCase));
        if (builtIn != null)
        {
            return FormatBuiltInNodeDoc(builtIn);
        }
        
        // Check custom nodes
        var customNode = LoadCustomNodeDefinition(nodeType);
        if (customNode != null)
        {
            return FormatCustomNodeDoc(customNode);
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets lightweight node summaries (type + name + one-line description).
    /// </summary>
    public List<NodeSummary> GetAllNodeSummaries()
    {
        var summaries = new List<NodeSummary>();
        
        // Built-in nodes
        foreach (var node in BuiltInNodeCatalogService.GetAllBuiltInNodeTypes())
        {
            summaries.Add(new NodeSummary(node.NodeTypeKey, node.DisplayName, node.Description, "BuiltIn"));
        }
        
        // Custom nodes
        var customNodesPath = Path.Combine(_env.ContentRootPath, "..", "custom-nodes");
        if (Directory.Exists(customNodesPath))
        {
            foreach (var file in Directory.GetFiles(customNodesPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var doc = JsonDocument.Parse(json);
                    var def = doc.RootElement.GetProperty("definition");
                    
                    var nodeTypeKey = def.GetProperty("nodeTypeKey").GetString() ?? "";
                    var displayName = def.GetProperty("displayName").GetString() ?? "";
                    var description = def.TryGetProperty("description", out var descProp) 
                        ? descProp.GetString() ?? "" 
                        : "";
                    
                    summaries.Add(new NodeSummary(nodeTypeKey, displayName, description, "Custom"));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse custom node: {File}", file);
                }
            }
        }
        
        return summaries;
    }
    
    /// <summary>
    /// Invalidates the cache, forcing a rebuild on next access.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedKnowledge = null;
            _lastCacheTime = DateTime.MinValue;
        }
        _logger.LogInformation("Node knowledge cache invalidated");
    }
    
    private string BuildNodeKnowledge()
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("# S2G Run Node Catalog");
        sb.AppendLine();
        sb.AppendLine("This document describes all available workflow nodes. Use this information to help users build and modify workflows.");
        sb.AppendLine();
        
        // Built-in nodes
        sb.AppendLine("## Built-in Nodes");
        sb.AppendLine();
        
        var builtInNodes = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes();
        var groupedBuiltIn = builtInNodes.GroupBy(n => n.Category);
        
        foreach (var group in groupedBuiltIn.OrderBy(g => g.Key))
        {
            sb.AppendLine($"### {group.Key}");
            sb.AppendLine();
            
            foreach (var node in group.OrderBy(n => n.DisplayName))
            {
                sb.AppendLine(FormatBuiltInNodeDoc(node));
                sb.AppendLine();
            }
        }
        
        // Custom nodes
        sb.AppendLine("## Custom Nodes");
        sb.AppendLine();
        
        var customNodes = LoadAllCustomNodes();
        var groupedCustom = customNodes.GroupBy(n => n.CategoryName ?? "Uncategorized");
        
        foreach (var group in groupedCustom.OrderBy(g => g.Key))
        {
            sb.AppendLine($"### {group.Key}");
            sb.AppendLine();
            
            foreach (var node in group.OrderBy(n => n.DisplayName))
            {
                sb.AppendLine(FormatCustomNodeDoc(node));
                sb.AppendLine();
            }
        }
        
        // Connection rules
        sb.AppendLine("## Connection Rules");
        sb.AppendLine();
        sb.AppendLine("- Nodes connect output → input via connection lines");
        sb.AppendLine("- Connection tags (labels) control flow branching:");
        sb.AppendLine("  - `true`/`false`: Condition node branches");
        sb.AppendLine("  - `success`/`error`: Most custom nodes have these");
        sb.AppendLine("  - `complete`/`error`: Async operation completion");
        sb.AppendLine("  - Custom tags can be triggered via `tags.trigger('tagname')` in scripts");
        sb.AppendLine();
        
        // Placeholder syntax
        sb.AppendLine("## Placeholder Syntax");
        sb.AppendLine();
        sb.AppendLine("Reference output from other nodes using: `{{NodeName.OutputParameter}}`");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("- `{{Listener.Body}}` - HTTP request body from a Listener node");
        sb.AppendLine("- `{{SqlQuery.Result}}` - Query result from a SQL node");
        sb.AppendLine("- `{{ArrayOps.Count}}` - Item count from Array Operations node");
        sb.AppendLine();
        
        // Workflow samples
        var samples = LoadWorkflowSamples();
        if (samples.Any())
        {
            sb.AppendLine("## Workflow Examples");
            sb.AppendLine();
            sb.AppendLine("Use these working examples as templates when building workflows:");
            sb.AppendLine();
            
            foreach (var sample in samples)
            {
                sb.AppendLine($"### {sample.Name}");
                if (!string.IsNullOrEmpty(sample.Description))
                {
                    sb.AppendLine(sample.Description);
                }
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(sample.Content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }
        
        _logger.LogInformation("Built node knowledge: {Chars} characters, {BuiltIn} built-in, {Custom} custom nodes, {Samples} workflow samples",
            sb.Length, builtInNodes.Count, customNodes.Count, samples.Count);
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Loads workflow samples from the workflow-samples directory.
    /// </summary>
    private List<WorkflowSampleInfo> LoadWorkflowSamples()
    {
        var samples = new List<WorkflowSampleInfo>();
        
        // Look for workflow-samples directory (relative to project root)
        var solutionRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        var samplesDir = Path.Combine(solutionRoot, "workflow-samples");
        
        if (!Directory.Exists(samplesDir))
        {
            _logger.LogDebug("No workflow-samples directory found at {Path}", samplesDir);
            return samples;
        }
        
        foreach (var file in Directory.GetFiles(samplesDir, "*.json"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var doc = JsonDocument.Parse(content);
                
                var name = doc.RootElement.TryGetProperty("Name", out var nameProp) 
                    ? nameProp.GetString() ?? Path.GetFileNameWithoutExtension(file)
                    : Path.GetFileNameWithoutExtension(file);
                    
                var description = doc.RootElement.TryGetProperty("Description", out var descProp) 
                    ? descProp.GetString() 
                    : null;
                
                samples.Add(new WorkflowSampleInfo(name, description, content));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load workflow sample: {File}", file);
            }
        }
        
        _logger.LogDebug("Loaded {Count} workflow samples", samples.Count);
        return samples;
    }
    
    private string FormatBuiltInNodeDoc(BuiltInNodeInfo node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#### {node.DisplayName} (`{node.NodeTypeKey}`)");
        if (!string.IsNullOrEmpty(node.Description))
        {
            sb.AppendLine(node.Description);
        }
        
        // Get output parameters from NodeHelper
        var outputs = NodeHelper.GetOutputParametersForType(node.NodeTypeKey);
        if (outputs.Any())
        {
            sb.AppendLine($"**Outputs:** {string.Join(", ", outputs)}");
        }
        
        return sb.ToString();
    }
    
    private string FormatCustomNodeDoc(NodeKnowledgeInfo node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#### {node.DisplayName} (`{node.NodeTypeKey}`)");
        
        if (!string.IsNullOrEmpty(node.Description))
        {
            sb.AppendLine(node.Description);
        }
        
        // Input fields
        if (node.InputFields.Any())
        {
            sb.AppendLine("**Inputs:**");
            foreach (var field in node.InputFields.OrderBy(f => f.DisplayOrder))
            {
                var required = field.IsRequired ? " (required)" : "";
                var type = field.FieldType;
                if (!string.IsNullOrEmpty(field.DropdownOptions))
                {
                    type = $"Dropdown: {field.DropdownOptions}";
                }
                sb.AppendLine($"  - `{field.FieldName}` ({type}{required}): {field.HelpText}");
            }
        }
        
        // Output parameters
        if (node.OutputParameters.Any())
        {
            var outputNames = node.OutputParameters.OrderBy(p => p.DisplayOrder).Select(p => p.ParameterName);
            sb.AppendLine($"**Outputs:** {string.Join(", ", outputNames)}");
        }
        
        // Connection tags
        if (node.ConnectionTags.Any())
        {
            var tagNames = node.ConnectionTags.OrderBy(t => t.DisplayOrder).Select(t => t.TagName);
            sb.AppendLine($"**Connection Tags:** {string.Join(", ", tagNames)}");
        }
        
        return sb.ToString();
    }
    
    private List<NodeKnowledgeInfo> LoadAllCustomNodes()
    {
        var nodes = new List<NodeKnowledgeInfo>();
        var customNodesPath = Path.Combine(_env.ContentRootPath, "..", "custom-nodes");
        
        if (!Directory.Exists(customNodesPath))
        {
            _logger.LogWarning("Custom nodes directory not found: {Path}", customNodesPath);
            return nodes;
        }
        
        foreach (var file in Directory.GetFiles(customNodesPath, "*.json"))
        {
            try
            {
                var node = ParseCustomNodeFile(file);
                if (node != null)
                {
                    nodes.Add(node);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse custom node file: {File}", file);
            }
        }
        
        return nodes;
    }
    
    private NodeKnowledgeInfo? LoadCustomNodeDefinition(string nodeType)
    {
        var customNodesPath = Path.Combine(_env.ContentRootPath, "..", "custom-nodes");
        if (!Directory.Exists(customNodesPath)) return null;
        
        foreach (var file in Directory.GetFiles(customNodesPath, "*.json"))
        {
            try
            {
                var node = ParseCustomNodeFile(file);
                if (node?.NodeTypeKey.Equals(nodeType, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return node;
                }
            }
            catch { }
        }
        
        return null;
    }
    
    private NodeKnowledgeInfo? ParseCustomNodeFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var doc = JsonDocument.Parse(json);
        
        if (!doc.RootElement.TryGetProperty("definition", out var def))
        {
            return null;
        }
        
        var node = new NodeKnowledgeInfo
        {
            NodeTypeKey = def.GetProperty("nodeTypeKey").GetString() ?? "",
            DisplayName = def.GetProperty("displayName").GetString() ?? "",
            Description = def.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
            CategoryName = def.TryGetProperty("categoryName", out var catProp) ? catProp.GetString() : null
        };
        
        // Parse input fields
        if (def.TryGetProperty("inputFields", out var inputFields))
        {
            foreach (var field in inputFields.EnumerateArray())
            {
                node.InputFields.Add(new NodeKnowledgeInputField
                {
                    FieldName = field.GetProperty("fieldName").GetString() ?? "",
                    DisplayLabel = field.TryGetProperty("displayLabel", out var dl) ? dl.GetString() : null,
                    HelpText = field.TryGetProperty("helpText", out var ht) ? ht.GetString() : null,
                    FieldType = field.TryGetProperty("fieldType", out var ft) ? ft.GetString() ?? "Text" : "Text",
                    IsRequired = field.TryGetProperty("isRequired", out var req) && req.GetBoolean(),
                    DropdownOptions = field.TryGetProperty("dropdownOptions", out var opts) ? opts.GetString() : null,
                    DisplayOrder = field.TryGetProperty("displayOrder", out var order) ? order.GetInt32() : 0
                });
            }
        }
        
        // Parse output parameters
        if (def.TryGetProperty("outputParameters", out var outputParams))
        {
            foreach (var param in outputParams.EnumerateArray())
            {
                node.OutputParameters.Add(new NodeKnowledgeOutputParam
                {
                    ParameterName = param.GetProperty("parameterName").GetString() ?? "",
                    Description = param.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    DataType = param.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "string" : "string",
                    DisplayOrder = param.TryGetProperty("displayOrder", out var order) ? order.GetInt32() : 0
                });
            }
        }
        
        // Parse connection tags
        if (def.TryGetProperty("connectionTags", out var connTags))
        {
            foreach (var tag in connTags.EnumerateArray())
            {
                node.ConnectionTags.Add(new NodeKnowledgeConnectionTag
                {
                    TagName = tag.GetProperty("tagName").GetString() ?? "",
                    Description = tag.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    DisplayOrder = tag.TryGetProperty("displayOrder", out var order) ? order.GetInt32() : 0
                });
            }
        }
        
        return node;
    }
}

#region DTOs

public record NodeSummary(string NodeTypeKey, string DisplayName, string Description, string Source);

public class NodeKnowledgeInfo
{
    public string NodeTypeKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string? CategoryName { get; set; }
    public List<NodeKnowledgeInputField> InputFields { get; set; } = new();
    public List<NodeKnowledgeOutputParam> OutputParameters { get; set; } = new();
    public List<NodeKnowledgeConnectionTag> ConnectionTags { get; set; } = new();
}

public class NodeKnowledgeInputField
{
    public string FieldName { get; set; } = "";
    public string? DisplayLabel { get; set; }
    public string? HelpText { get; set; }
    public string FieldType { get; set; } = "Text";
    public bool IsRequired { get; set; }
    public string? DropdownOptions { get; set; }
    public int DisplayOrder { get; set; }
}

public class NodeKnowledgeOutputParam
{
    public string ParameterName { get; set; } = "";
    public string? Description { get; set; }
    public string DataType { get; set; } = "string";
    public int DisplayOrder { get; set; }
}

public class NodeKnowledgeConnectionTag
{
    public string TagName { get; set; } = "";
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
}

/// <summary>
/// Holds workflow sample data for AI context.
/// </summary>
public record WorkflowSampleInfo(string Name, string? Description, string Content);

#endregion
