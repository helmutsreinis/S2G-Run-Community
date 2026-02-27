using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Workflow node for reading/writing the Knowledge Base.
/// Allows AI agents and workflows to programmatically interact with the KB.
/// </summary>
public class KnowledgeNode : BaseNodeExecutor
{
    private readonly KnowledgeBaseService _kbService;

    public KnowledgeNode(NodeExecutionManager executionManager, KnowledgeBaseService kbService)
        : base(executionManager)
    {
        _kbService = kbService;
    }

    public override string NodeType => "Knowledge";

    public override List<string> GetOutputParameters() =>
        new() { "Result", "ResultJson", "EntityId", "RelationsJson", "GraphJson", "Success" };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        // Parse configuration
        var configJson = node.Configuration ?? "{}";
        var config = JsonSerializer.Deserialize<KnowledgeNodeConfig>(configJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new KnowledgeNodeConfig();

        // Resolve placeholders in all string fields
        config.Operation = ReplacePlaceholders(config.Operation ?? "Search", inputData);
        config.EntityId = ReplacePlaceholders(config.EntityId, inputData);
        config.Title = ReplacePlaceholders(config.Title, inputData);
        config.Content = ReplacePlaceholders(config.Content, inputData);
        config.EntityType = ReplacePlaceholders(config.EntityType ?? "Note", inputData);
        config.Tags = ReplacePlaceholders(config.Tags, inputData);
        config.Properties = ReplacePlaceholders(config.Properties, inputData);
        config.SourceId = ReplacePlaceholders(config.SourceId, inputData);
        config.TargetId = ReplacePlaceholders(config.TargetId, inputData);
        config.RelationType = ReplacePlaceholders(config.RelationType ?? "related_to", inputData);
        config.Query = ReplacePlaceholders(config.Query, inputData);
        config.Direction = ReplacePlaceholders(config.Direction ?? "both", inputData);

        // Determine organization scope from input data
        Guid? orgId = null;
        if (inputData.TryGetValue("_OrganizationId", out var orgIdObj) &&
            orgIdObj is string orgIdStr && Guid.TryParse(orgIdStr, out var parsedOrgId))
            orgId = parsedOrgId;

        // Resolve connection string
        var connectionString = await _kbService.GetConnectionStringAsync(userId, orgId);
        if (string.IsNullOrEmpty(connectionString))
        {
            var missingScope = orgId.HasValue ? "this organization" : "your account";
            Log(node, NodeLogLevel.Warning,
                $"No Knowledge Account configured for {missingScope}. " +
                $"Go to Settings → Knowledge Base (personal) or Organization → Settings → Knowledge Base (org).");

            return new NodeExecutionResult
            {
                Success = false,
                OutputData = new Dictionary<string, object?>
                {
                    ["Success"] = false,
                    ["Result"] = $"❌ No Knowledge Account connection string configured for {missingScope}.",
                    ["_TriggeredTags"] = new[] { "error" }
                }
            };
        }

        var scopePrefix = _kbService.GetScopePrefix(userId, orgId);

        // Initialize tables/containers on first use
        try
        {
            await _kbService.InitializeTablesAsync(connectionString, scopePrefix);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Failed to initialize Knowledge Base tables: {ex.Message}");
            return ErrorResult("Failed to initialize Knowledge Base storage.");
        }

        Log(node, NodeLogLevel.Info, $"Knowledge node executing: {config.Operation} (scope: {scopePrefix})");

        try
        {
            return config.Operation switch
            {
                "Search" => await ExecuteSearchAsync(connectionString, scopePrefix, config),
                "GetEntity" => await ExecuteGetEntityAsync(connectionString, scopePrefix, config),
                "AddEntity" => await ExecuteAddEntityAsync(connectionString, scopePrefix, config, userId),
                "UpdateEntity" => await ExecuteUpdateEntityAsync(connectionString, scopePrefix, config, userId),
                "DeleteEntity" => await ExecuteDeleteEntityAsync(connectionString, scopePrefix, config),
                "AddRelation" => await ExecuteAddRelationAsync(connectionString, scopePrefix, config, userId),
                "RemoveRelation" => await ExecuteRemoveRelationAsync(connectionString, scopePrefix, config),
                "GetRelations" => await ExecuteGetRelationsAsync(connectionString, scopePrefix, config),
                "GetNeighbors" => await ExecuteGetNeighborsAsync(connectionString, scopePrefix, config),
                "GetGraph" => await ExecuteGetGraphAsync(connectionString, scopePrefix, config),
                "ListEntities" => await ExecuteListEntitiesAsync(connectionString, scopePrefix, config),
                _ => ErrorResult($"Unknown operation: {config.Operation}")
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Knowledge node error ({config.Operation}): {ex.Message}");
            return ErrorResult($"Knowledge operation failed: {ex.Message}");
        }
    }

    // ─── Operation Handlers ──────────────────────────────────────────────────

    private async Task<NodeExecutionResult> ExecuteSearchAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        var results = await _kbService.SearchAsync(conn, scope, config.Query ?? "", config.MaxResults);

        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results for \"{config.Query}\"");
        sb.AppendLine($"Found {results.Count} result(s):\n");
        foreach (var r in results)
        {
            sb.AppendLine($"### {r.Title} `[{r.EntityType}]` (id: `{r.EntityId}`)");
            if (!string.IsNullOrEmpty(r.Summary))
                sb.AppendLine(r.Summary?.Substring(0, Math.Min(200, r.Summary.Length)));
            sb.AppendLine();
        }

        return SuccessResult(sb.ToString(), JsonSerializer.Serialize(results), entityId: results.FirstOrDefault()?.EntityId);
    }

    private async Task<NodeExecutionResult> ExecuteGetEntityAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        if (string.IsNullOrEmpty(config.EntityId))
            return ErrorResult("EntityId is required for GetEntity.");

        var entity = await _kbService.GetEntityAsync(conn, scope, config.EntityId);
        if (entity == null)
            return ErrorResult($"Entity '{config.EntityId}' not found.");

        var sb = new StringBuilder();
        sb.AppendLine($"# {entity.Title}");
        sb.AppendLine($"**Type:** {entity.EntityType}  |  **ID:** `{entity.Id}`");
        if (entity.Tags.Any()) sb.AppendLine($"**Tags:** {string.Join(", ", entity.Tags)}");
        if (entity.Properties.Any())
        {
            sb.AppendLine("**Properties:**");
            foreach (var kv in entity.Properties)
                sb.AppendLine($"- {kv.Key}: {kv.Value}");
        }
        sb.AppendLine();
        sb.AppendLine(entity.Content ?? "*(no content)*");

        return SuccessResult(sb.ToString(), JsonSerializer.Serialize(entity), entityId: entity.Id);
    }

    private async Task<NodeExecutionResult> ExecuteAddEntityAsync(
        string conn, string scope, KnowledgeNodeConfig config, string userId)
    {
        if (string.IsNullOrEmpty(config.Title))
            return ErrorResult("Title is required for AddEntity.");

        var tags = ParseTags(config.Tags);
        var properties = ParseProperties(config.Properties);

        var entity = await _kbService.AddEntityAsync(conn, scope,
            config.Title, config.Content ?? "", config.EntityType ?? "Note",
            tags, properties, userId);

        return SuccessResult(
            $"✅ Entity **{entity.Title}** created with ID `{entity.Id}`.",
            JsonSerializer.Serialize(entity),
            entityId: entity.Id);
    }

    private async Task<NodeExecutionResult> ExecuteUpdateEntityAsync(
        string conn, string scope, KnowledgeNodeConfig config, string userId)
    {
        if (string.IsNullOrEmpty(config.EntityId))
            return ErrorResult("EntityId is required for UpdateEntity.");

        var tags = string.IsNullOrEmpty(config.Tags) ? null : ParseTags(config.Tags);
        var properties = string.IsNullOrEmpty(config.Properties) ? null : ParseProperties(config.Properties);

        var entity = await _kbService.UpdateEntityAsync(conn, scope, config.EntityId,
            config.Title, config.Content, config.EntityType, tags, properties, userId);

        return SuccessResult(
            $"✅ Entity **{entity.Title}** updated.",
            JsonSerializer.Serialize(entity),
            entityId: entity.Id);
    }

    private async Task<NodeExecutionResult> ExecuteDeleteEntityAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        if (string.IsNullOrEmpty(config.EntityId))
            return ErrorResult("EntityId is required for DeleteEntity.");

        await _kbService.DeleteEntityAsync(conn, scope, config.EntityId);
        return SuccessResult($"✅ Entity `{config.EntityId}` deleted.", "{\"deleted\":true}", entityId: config.EntityId);
    }

    private async Task<NodeExecutionResult> ExecuteAddRelationAsync(
        string conn, string scope, KnowledgeNodeConfig config, string userId)
    {
        if (string.IsNullOrEmpty(config.SourceId) || string.IsNullOrEmpty(config.TargetId))
            return ErrorResult("SourceId and TargetId are required for AddRelation.");

        await _kbService.AddRelationAsync(conn, scope,
            config.SourceId, config.TargetId,
            config.RelationType ?? "related_to",
            config.Bidirectional, null, userId);

        return SuccessResult(
            $"✅ Relation `{config.RelationType}` added: `{config.SourceId}` → `{config.TargetId}`.",
            $"{{\"source\":\"{config.SourceId}\",\"target\":\"{config.TargetId}\",\"type\":\"{config.RelationType}\"}}");
    }

    private async Task<NodeExecutionResult> ExecuteRemoveRelationAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        if (string.IsNullOrEmpty(config.SourceId) || string.IsNullOrEmpty(config.TargetId))
            return ErrorResult("SourceId and TargetId are required for RemoveRelation.");

        await _kbService.RemoveRelationAsync(conn, scope,
            config.SourceId, config.TargetId, config.RelationType ?? "related_to");

        return SuccessResult($"✅ Relation removed.", "{\"removed\":true}");
    }

    private async Task<NodeExecutionResult> ExecuteGetRelationsAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        if (string.IsNullOrEmpty(config.EntityId))
            return ErrorResult("EntityId is required for GetRelations.");

        var relations = await _kbService.GetRelationsAsync(conn, scope, config.EntityId, config.Direction);

        var sb = new StringBuilder();
        sb.AppendLine($"## Relations for `{config.EntityId}`");
        foreach (var r in relations)
            sb.AppendLine($"- **{r.RelationType}**: `{r.SourceId}` → `{r.TargetId}` ({(r.Bidirectional ? "bidirectional" : "directed")})");

        return SuccessResult(sb.ToString(), JsonSerializer.Serialize(relations), relationsJson: JsonSerializer.Serialize(relations));
    }

    private async Task<NodeExecutionResult> ExecuteGetNeighborsAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        if (string.IsNullOrEmpty(config.EntityId))
            return ErrorResult("EntityId is required for GetNeighbors.");

        var subgraph = await _kbService.GetNeighborsAsync(conn, scope, config.EntityId, config.Depth);
        var sb = new StringBuilder();
        sb.AppendLine($"## Neighbors of `{config.EntityId}` (depth {config.Depth})");
        sb.AppendLine($"Found {subgraph.Nodes.Count} node(s), {subgraph.Edges.Count} edge(s).");
        foreach (var n in subgraph.Nodes)
            sb.AppendLine($"- **{n.Title}** [{n.EntityType}] `{n.Id}`");

        return SuccessResult(sb.ToString(), JsonSerializer.Serialize(subgraph), graphJson: JsonSerializer.Serialize(subgraph));
    }

    private async Task<NodeExecutionResult> ExecuteGetGraphAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        var graph = await _kbService.GetGraphAsync(conn, scope,
            string.IsNullOrEmpty(config.EntityType) ? null : config.EntityType,
            null,
            config.MaxNodes > 0 ? config.MaxNodes : 200);

        var sb = new StringBuilder();
        sb.AppendLine($"## Knowledge Graph");
        sb.AppendLine($"**{graph.Nodes.Count} nodes**, **{graph.Edges.Count} edges**");

        return SuccessResult(sb.ToString(), JsonSerializer.Serialize(graph), graphJson: JsonSerializer.Serialize(graph));
    }

    private async Task<NodeExecutionResult> ExecuteListEntitiesAsync(
        string conn, string scope, KnowledgeNodeConfig config)
    {
        var entities = await _kbService.ListEntitiesAsync(conn, scope,
            string.IsNullOrEmpty(config.EntityType) ? null : config.EntityType,
            null,
            config.MaxResults > 0 ? config.MaxResults : 100);

        var sb = new StringBuilder();
        sb.AppendLine($"## Entity List ({entities.Count} items)");
        foreach (var e in entities)
        {
            sb.AppendLine($"- **{e.Title}** [{e.EntityType}] `{e.Id}`");
            if (e.Tags.Any()) sb.Append($"  tags: {string.Join(", ", e.Tags)}");
        }

        return SuccessResult(sb.ToString(), JsonSerializer.Serialize(entities), entityId: entities.FirstOrDefault()?.Id);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static NodeExecutionResult SuccessResult(
        string markdown, string resultJson,
        string? entityId = null,
        string? relationsJson = null,
        string? graphJson = null)
    {
        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                ["Result"] = markdown,
                ["ResultJson"] = resultJson,
                ["EntityId"] = entityId,
                ["RelationsJson"] = relationsJson,
                ["GraphJson"] = graphJson,
                ["Success"] = true,
                ["_TriggeredTags"] = new[] { "complete" }
            }
        };
    }

    private static NodeExecutionResult ErrorResult(string message)
    {
        return new NodeExecutionResult
        {
            Success = false,
            OutputData = new Dictionary<string, object?>
            {
                ["Result"] = $"❌ {message}",
                ["ResultJson"] = "{}",
                ["Success"] = false,
                ["_TriggeredTags"] = new[] { "error" }
            }
        };
    }

    private static List<string> ParseTags(string? tagsStr)
    {
        if (string.IsNullOrWhiteSpace(tagsStr)) return new();
        return tagsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
    }

    private static Dictionary<string, object>? ParseProperties(string? propsStr)
    {
        if (string.IsNullOrWhiteSpace(propsStr)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, object>>(propsStr); }
        catch { return null; }
    }

    private static string? ReplacePlaceholders(string? value, Dictionary<string, object?> inputData)
    {
        if (string.IsNullOrEmpty(value)) return value;
        foreach (var (key, val) in inputData)
        {
            if (val != null)
                value = value.Replace($"{{{{{key}}}}}", val.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        return value;
    }
}
