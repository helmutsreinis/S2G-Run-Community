using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// REST API for browsing the node catalog (built-in + custom nodes).
/// </summary>
[ApiController]
[Route("api/v1/catalog")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class CatalogApiController : ControllerBase
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public CatalogApiController(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>List all node categories with node counts.</summary>
    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        var builtIn = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes();
        var builtInCategories = builtIn
            .GroupBy(n => n.Category)
            .Select(g => new
            {
                name = g.Key,
                type = "builtin",
                nodeCount = g.Count()
            }).ToList();

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var customCategories = await context.CustomNodeCategories
            .Include(c => c.Nodes)
            .Where(c => c.IsEnabled)
            .Select(c => new
            {
                name = c.Name,
                type = "custom",
                nodeCount = c.Nodes.Count(n => n.IsEnabled)
            }).ToListAsync();

        var all = builtInCategories
            .Cast<object>()
            .Concat(customCategories)
            .ToList();

        return Ok(all);
    }

    /// <summary>List all available nodes (built-in + custom) with descriptions and connection tags.</summary>
    [HttpGet("nodes")]
    public async Task<IActionResult> GetAllNodes()
    {
        var builtIn = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes()
            .Select(n => new CatalogNodeResponse
            {
                NodeType = n.NodeTypeKey,
                DisplayName = n.DisplayName,
                Icon = n.Icon,
                Category = n.Category,
                Description = n.Description,
                IsBuiltIn = true
            }).ToList();

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var customNodes = await context.CustomNodeDefinitions
            .Include(n => n.Category)
            .Include(n => n.ConnectionTags)
            .Where(n => n.IsEnabled)
            .ToListAsync();

        var custom = customNodes.Select(n => new CatalogNodeResponse
        {
            NodeType = $"Custom_{n.NodeTypeKey}",
            DisplayName = n.DisplayName,
            Icon = n.IconFallbackEmoji ?? "🔧",
            Category = n.Category?.Name ?? "Custom",
            Description = n.Description ?? "",
            IsBuiltIn = false,
            ConnectionTags = n.ConnectionTags?.Select(t => new ConnectionTagResponse
            {
                TagName = t.TagName,
                Description = t.Description,
                Color = t.Color,
                DisplayOrder = t.DisplayOrder
            }).ToList() ?? new()
        }).ToList();

        return Ok(builtIn.Concat(custom));
    }

    /// <summary>List nodes in a specific category.</summary>
    [HttpGet("categories/{categoryName}/nodes")]
    public async Task<IActionResult> GetNodesByCategory(string categoryName)
    {
        var builtIn = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes()
            .Where(n => n.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
            .Select(n => new CatalogNodeResponse
            {
                NodeType = n.NodeTypeKey,
                DisplayName = n.DisplayName,
                Icon = n.Icon,
                Category = n.Category,
                Description = n.Description,
                IsBuiltIn = true
            }).ToList();

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var customNodes = await context.CustomNodeDefinitions
            .Include(n => n.Category)
            .Include(n => n.ConnectionTags)
            .Where(n => n.IsEnabled && n.Category != null &&
                        n.Category.Name.ToLower() == categoryName.ToLower())
            .ToListAsync();

        var custom = customNodes.Select(n => new CatalogNodeResponse
        {
            NodeType = $"Custom_{n.NodeTypeKey}",
            DisplayName = n.DisplayName,
            Icon = n.IconFallbackEmoji ?? "🔧",
            Category = n.Category?.Name ?? "Custom",
            Description = n.Description ?? "",
            IsBuiltIn = false,
            ConnectionTags = n.ConnectionTags?.Select(t => new ConnectionTagResponse
            {
                TagName = t.TagName,
                Description = t.Description,
                Color = t.Color,
                DisplayOrder = t.DisplayOrder
            }).ToList() ?? new()
        }).ToList();

        return Ok(builtIn.Concat(custom));
    }

    /// <summary>Get full schema for a specific node type (built-in or custom).</summary>
    [HttpGet("nodes/{type}/schema")]
    public async Task<IActionResult> GetNodeSchema(string type)
    {
        // ── Try built-in first ──────────────────────────────────────
        var builtIn = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes()
            .FirstOrDefault(n => n.NodeTypeKey.Equals(type, StringComparison.OrdinalIgnoreCase));

        if (builtIn != null)
        {
            var outputs = Components.Pages.Workflow.Designer.NodeHelper
                .GetOutputParametersForType(builtIn.NodeTypeKey);

            var tags = GetBuiltInConnectionTags(builtIn.NodeTypeKey);

            return Ok(new NodeSchemaResponse
            {
                NodeType = builtIn.NodeTypeKey,
                DisplayName = builtIn.DisplayName,
                Icon = builtIn.Icon,
                Category = builtIn.Category,
                Description = builtIn.Description,
                IsBuiltIn = true,
                OutputParameters = outputs.Select(o => new SchemaOutputParameter
                {
                    ParameterName = o,
                    DataType = "string",
                }).ToList(),
                ConnectionTags = tags.Select(t => new SchemaConnectionTag { TagName = t }).ToList()
            });
        }

        // ── Try custom node (DB) ────────────────────────────────────
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Normalise: accept both "Custom_Foo" and "Foo"
        var rawKey = type.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase)
            ? type[7..] : type;

        var customNode = await context.CustomNodeDefinitions
            .Include(n => n.InputFields)
            .Include(n => n.OutputParameters)
            .Include(n => n.ConnectionTags)
            .Include(n => n.Category)
            .Where(n => n.IsEnabled)
            .FirstOrDefaultAsync(n =>
                n.NodeTypeKey.ToLower() == rawKey.ToLower() ||
                n.NodeTypeKey.ToLower() == type.ToLower());

        if (customNode == null)
            return NotFound(new { error = $"Node type '{type}' not found. Use GET /api/v1/catalog/nodes to list available types." });

        return Ok(new NodeSchemaResponse
        {
            NodeType = customNode.NodeTypeKey,
            DisplayName = customNode.DisplayName,
            Icon = customNode.IconFallbackEmoji ?? "🔧",
            Category = customNode.Category?.Name ?? "Custom",
            Description = customNode.Description ?? "",
            IsBuiltIn = false,
            ExecutionType = customNode.ExecutionType.ToString(),
            TimeoutSeconds = customNode.TimeoutSeconds,
            DefaultConfiguration = customNode.DefaultConfigurationJson,
            InputFields = customNode.InputFields
                .OrderBy(f => f.DisplayOrder)
                .Select(f => new SchemaInputField
                {
                    FieldName = f.FieldName,
                    DisplayLabel = f.DisplayLabel,
                    HelpText = f.HelpText,
                    FieldType = f.FieldType.ToString(),
                    DefaultValue = f.DefaultValue,
                    IsRequired = f.IsRequired,
                    AllowPlaceholders = f.AllowPlaceholders,
                    ValidationRegex = f.ValidationRegex,
                    ExpectedDataType = f.ExpectedDataType,
                    SelectOptions = f.SelectOptionsJson
                }).ToList(),
            OutputParameters = customNode.OutputParameters
                .OrderBy(p => p.DisplayOrder)
                .Select(p => new SchemaOutputParameter
                {
                    ParameterName = p.ParameterName,
                    Description = p.Description,
                    DataType = p.DataType
                }).ToList(),
            ConnectionTags = customNode.ConnectionTags
                .OrderBy(t => t.DisplayOrder)
                .Select(t => new SchemaConnectionTag
                {
                    TagName = t.TagName,
                    Description = t.Description,
                    Color = t.Color
                }).ToList()
        });
    }

    /// <summary>Built-in node connection tags used for routing and auto-labeling.</summary>
    private static List<string> GetBuiltInConnectionTags(string nodeType) => nodeType switch
    {
        // Flow control
        "Condition" => new() { "true", "false" },
        "Aggregator" => new() { "valid", "invalid" },
        "Loop" => new() { "Loop Array" },

        // Data storage
        "StorageClient" => new() { "storage", "reader" },
        "VectorClient" => new() { "storage", "reader" },

        // AI agents (tool calling via connections)
        "DeepSeekAgent" => new() { "tool:*", "agent" },
        "CopilotAgent" => new() { "tool:*", "agent" },
        "OpenClaw" => new() { "tool:*", "agent", "trigger" },
        "Anthropic" => new() { "tool:*", "agent" },

        // AI providers (agent label when connected to Orchestrator)
        "OpenAI" => new() { "agent" },
        "DeepSeek" => new() { "agent", "orchestrate" },
        "Gemini" => new() { "agent" },
        "Mistral" => new() { "agent" },
        "Groq" => new() { "agent" },

        // Orchestration
        "Orchestrator" => new() { "orchestrate" },

        // Remote execution
        "RemoteCommand" => new() { "run:rm-*" },

        _ => new()
    };
}

public class NodeSchemaResponse
{
    public string NodeType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public string? ExecutionType { get; set; }
    public int? TimeoutSeconds { get; set; }
    public string? DefaultConfiguration { get; set; }
    public List<SchemaInputField> InputFields { get; set; } = new();
    public List<SchemaOutputParameter> OutputParameters { get; set; } = new();
    public List<SchemaConnectionTag> ConnectionTags { get; set; } = new();
}

public class SchemaInputField
{
    public string FieldName { get; set; } = "";
    public string? DisplayLabel { get; set; }
    public string? HelpText { get; set; }
    public string FieldType { get; set; } = "Text";
    public string? DefaultValue { get; set; }
    public bool IsRequired { get; set; }
    public bool AllowPlaceholders { get; set; } = true;
    public string? ValidationRegex { get; set; }
    public string? ExpectedDataType { get; set; }
    public string? SelectOptions { get; set; }
}

public class SchemaOutputParameter
{
    public string ParameterName { get; set; } = "";
    public string? Description { get; set; }
    public string DataType { get; set; } = "string";
}

public class SchemaConnectionTag
{
    public string TagName { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
}

public class CatalogNodeResponse
{
    public string NodeType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public List<ConnectionTagResponse> ConnectionTags { get; set; } = new();
}

public class ConnectionTagResponse
{
    public string TagName { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public int DisplayOrder { get; set; }
}

