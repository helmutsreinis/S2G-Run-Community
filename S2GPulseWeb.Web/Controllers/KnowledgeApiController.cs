using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// REST API for Knowledge Base entity, relation, and graph operations.
/// All endpoints respect the authenticated user's active personal/organization scope.
/// </summary>
[ApiController]
[Route("api/v1/knowledge")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class KnowledgeApiController : ControllerBase
{
    private readonly KnowledgeBaseService _kb;
    private readonly OrganizationService _org;
    private readonly ILogger<KnowledgeApiController> _logger;

    public KnowledgeApiController(
        KnowledgeBaseService kb,
        OrganizationService org,
        ILogger<KnowledgeApiController> logger)
    {
        _kb = kb;
        _org = org;
        _logger = logger;
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>
    /// Resolves the connection string and scope prefix for the current user's active context.
    /// Returns null connection string if the user hasn't configured a Knowledge Account.
    /// </summary>
    private async Task<(string? cs, string scope, Guid? orgId)> GetContextAsync()
    {
        var userId = GetUserId();
        var activeOrgId = await _org.GetActiveOrganizationIdAsync(userId);
        var userOrgs = await _org.GetUserOrganizationsAsync(userId);
        var orgId = userOrgs.Any(o => o.Id == activeOrgId) ? activeOrgId : (Guid?)null;
        var cs = await _kb.GetConnectionStringAsync(userId, orgId);
        var scope = _kb.GetScopePrefix(userId, orgId);
        return (cs, scope, orgId);
    }

    // ── Entity Endpoints ──────────────────────────────────────────────────

    /// <summary>List entities with cursor-based pagination, optionally filtered by type, tag, or search.</summary>
    [HttpGet("entities")]
    public async Task<IActionResult> ListEntities(
        [FromQuery] string? type = null,
        [FromQuery] string? tag = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null)
    {
        if (limit < 1 || limit > 500)
            return BadRequest(new { error = "limit must be between 1 and 500." });

        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured. Add an Azure Storage connection string in Settings." });

        await _kb.InitializeTablesAsync(cs, scope);

        // Search path: in-memory filtering, no cursor support
        if (!string.IsNullOrWhiteSpace(search))
        {
            var all = await _kb.ListEntitiesAsync(cs, scope, null, null, Math.Max(limit * 10, 1000));
            var lower = search.ToLower();
            var searchResults = all
                .Where(e => e.Title.ToLower().Contains(lower) ||
                            (e.Summary?.ToLower().Contains(lower) ?? false) ||
                            (e.Content?.ToLower().Contains(lower) ?? false))
                .Take(limit)
                .ToList();
            // Search returns flat array (no cursor — in-memory filtering has no stable page boundary)
            return Ok(searchResults);
        }

        var (items, nextCursor) = await _kb.ListEntitiesPagedAsync(cs, scope, type, tag, limit, cursor);
        return Ok(new
        {
            data = items,
            pagination = new { limit, nextCursor }
        });
    }

    /// <summary>Get a single entity by ID, including its full content.</summary>
    [HttpGet("entities/{id}")]
    public async Task<IActionResult> GetEntity(string id)
    {
        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        var entity = await _kb.GetEntityAsync(cs, scope, id);
        if (entity == null) return NotFound(new { error = $"Entity '{id}' not found." });
        return Ok(entity);
    }

    /// <summary>Create a new entity.</summary>
    [HttpPost("entities")]
    public async Task<IActionResult> CreateEntity([FromBody] CreateEntityRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        // Ensure tables AND blob container exist before any write
        await _kb.InitializeTablesAsync(cs, scope);

        var entity = await _kb.AddEntityAsync(
            cs, scope,
            req.Title.Trim(),
            req.Content ?? "",
            string.IsNullOrWhiteSpace(req.EntityType) ? "Note" : req.EntityType.Trim(),
            req.Tags ?? new(),
            req.Properties,
            GetUserId());

        return CreatedAtAction(nameof(GetEntity), new { id = entity.Id }, entity);
    }

    /// <summary>Update an existing entity.</summary>
    [HttpPut("entities/{id}")]
    public async Task<IActionResult> UpdateEntity(string id, [FromBody] UpdateEntityRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "title is required." });

        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        var existing = await _kb.GetEntityAsync(cs, scope, id);
        if (existing == null) return NotFound(new { error = $"Entity '{id}' not found." });

        // Ensure blob container exists before writing
        await _kb.InitializeTablesAsync(cs, scope);

        try
        {
            var updated = await _kb.UpdateEntityAsync(
                cs, scope, id,
                req.Title.Trim(),
                req.Content ?? existing.Content ?? "",
                string.IsNullOrWhiteSpace(req.EntityType) ? existing.EntityType : req.EntityType.Trim(),
                req.Tags ?? existing.Tags,
                req.Properties ?? existing.Properties,
                GetUserId());
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Delete an entity and all its relations.</summary>
    [HttpDelete("entities/{id}")]
    public async Task<IActionResult> DeleteEntity(string id)
    {
        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        var existing = await _kb.GetEntityAsync(cs, scope, id);
        if (existing == null) return NotFound(new { error = $"Entity '{id}' not found." });

        await _kb.DeleteEntityAsync(cs, scope, id);
        return Ok(new { message = $"Entity '{id}' deleted." });
    }

    // ── Relation Endpoints ────────────────────────────────────────────────

    /// <summary>List relations for an entity with cursor-based pagination (cursor only supported for direction=outgoing).</summary>
    [HttpGet("entities/{id}/relations")]
    public async Task<IActionResult> GetRelations(
        string id,
        [FromQuery] string direction = "both",
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null)
    {
        if (direction is not ("both" or "incoming" or "outgoing"))
            return BadRequest(new { error = "direction must be 'both', 'incoming', or 'outgoing'." });
        if (limit < 1 || limit > 500)
            return BadRequest(new { error = "limit must be between 1 and 500." });

        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        // Return 404 if the entity itself doesn't exist (distinguishes from "exists but has no relations")
        var entity = await _kb.GetEntityAsync(cs, scope, id);
        if (entity == null) return NotFound(new { error = $"Entity '{id}' not found." });

        var (items, nextCursor) = await _kb.GetRelationsPagedAsync(cs, scope, id, direction, limit, cursor);
        return Ok(new
        {
            data = items,
            pagination = new { limit, nextCursor }
        });
    }

    /// <summary>Create a relation between two entities.</summary>
    [HttpPost("relations")]
    public async Task<IActionResult> AddRelation([FromBody] AddRelationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SourceId))
            return BadRequest(new { error = "sourceId is required." });
        if (string.IsNullOrWhiteSpace(req.TargetId))
            return BadRequest(new { error = "targetId is required." });
        if (string.IsNullOrWhiteSpace(req.RelationType))
            return BadRequest(new { error = "relationType is required." });

        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        // Validate both entities exist to prevent ghost/orphan relations
        var source = await _kb.GetEntityAsync(cs, scope, req.SourceId);
        if (source == null) return NotFound(new { error = $"Source entity '{req.SourceId}' not found." });
        var target = await _kb.GetEntityAsync(cs, scope, req.TargetId);
        if (target == null) return NotFound(new { error = $"Target entity '{req.TargetId}' not found." });

        await _kb.AddRelationAsync(
            cs, scope,
            req.SourceId, req.TargetId,
            req.RelationType.Trim(),
            req.Bidirectional,
            null,
            GetUserId());

        return Ok(new
        {
            message = "Relation created.",
            sourceId = req.SourceId,
            targetId = req.TargetId,
            relationType = req.RelationType,
            bidirectional = req.Bidirectional
        });
    }

    /// <summary>Remove a relation between two entities.</summary>
    [HttpDelete("relations")]
    public async Task<IActionResult> RemoveRelation([FromBody] RemoveRelationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SourceId) ||
            string.IsNullOrWhiteSpace(req.TargetId) ||
            string.IsNullOrWhiteSpace(req.RelationType))
            return BadRequest(new { error = "sourceId, targetId, and relationType are all required." });

        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        await _kb.RemoveRelationAsync(cs, scope, req.SourceId, req.TargetId, req.RelationType);
        return Ok(new { message = "Relation removed." });
    }

    // ── Graph Endpoint ────────────────────────────────────────────────────

    /// <summary>Get the knowledge graph, optionally filtered by entity type or tag.</summary>
    [HttpGet("graph")]
    public async Task<IActionResult> GetGraph(
        [FromQuery] string? type = null,
        [FromQuery] string? tag = null,
        [FromQuery] int maxNodes = 200)
    {
        var (cs, scope, _) = await GetContextAsync();
        if (string.IsNullOrEmpty(cs))
            return BadRequest(new { error = "Knowledge Account not configured." });

        var graph = await _kb.GetGraphAsync(cs, scope, type, tag, maxNodes);
        return Ok(graph);
    }
}

// ── Request DTOs ───────────────────────────────────────────────────────────
// All required string fields are declared nullable (string?) so the ASP.NET
// model binder never intercepts with a ProblemDetails 400. Our manual
// IsNullOrWhiteSpace checks handle both missing-field (null) and empty-string
// inputs, returning a consistent { "error": "..." } payload in both cases.

/// <summary>Request body for POST /api/v1/knowledge/entities</summary>
public record CreateEntityRequest(
    string? Title,
    string? Content,
    string? EntityType,
    List<string>? Tags,
    Dictionary<string, object>? Properties);

/// <summary>Request body for PUT /api/v1/knowledge/entities/{id}</summary>
public record UpdateEntityRequest(
    string? Title,
    string? Content,
    string? EntityType,
    List<string>? Tags,
    Dictionary<string, object>? Properties);

/// <summary>Request body for POST /api/v1/knowledge/relations</summary>
public record AddRelationRequest(
    string? SourceId,
    string? TargetId,
    string? RelationType,
    bool Bidirectional = false);

/// <summary>Request body for DELETE /api/v1/knowledge/relations</summary>
public record RemoveRelationRequest(
    string? SourceId,
    string? TargetId,
    string? RelationType);
