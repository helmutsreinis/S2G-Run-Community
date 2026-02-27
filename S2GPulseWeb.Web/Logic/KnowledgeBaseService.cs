using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Core service for the Knowledge Base feature.
/// Stores entity metadata in Azure Table Storage and full content in Azure Blob Storage.
/// Scoped per user (personal) or organization (shared).
/// </summary>
public class KnowledgeBaseService
{
    private readonly UserSecretService _secretService;

    public KnowledgeBaseService(UserSecretService secretService)
    {
        _secretService = secretService;
    }

    // ─── Connection & Scope ──────────────────────────────────────────────────

    /// <summary>
    /// Gets the Knowledge Account connection string.
    /// Checks organization first (if orgId provided), then personal.
    /// </summary>
    public async Task<string?> GetConnectionStringAsync(string userId, Guid? organizationId = null)
    {
        if (organizationId.HasValue)
        {
            var orgConn = await _secretService.GetSecretAsync(userId, "Knowledge_Account", organizationId);
            if (!string.IsNullOrEmpty(orgConn)) return orgConn;
        }
        return await _secretService.GetSecretAsync(userId, "Knowledge_Account");
    }

    /// <summary>
    /// Derives the scope prefix for table/container names.
    /// Azure Table names: alphanumeric only, 3-63 chars, cannot start with a number.
    /// </summary>
    public string GetScopePrefix(string userId, Guid? organizationId = null)
    {
        if (organizationId.HasValue)
            return $"kborg{organizationId.Value.ToString("N").Substring(0, 8)}";

        var safeId = userId.Replace("-", "");
        return $"kb{safeId.Substring(0, Math.Min(8, safeId.Length))}";
    }

    // ─── Initialization ──────────────────────────────────────────────────────

    public async Task InitializeTablesAsync(string connectionString, string scopePrefix)
    {
        var tableService = new TableServiceClient(connectionString);
        await tableService.CreateTableIfNotExistsAsync($"{scopePrefix}entities");
        await tableService.CreateTableIfNotExistsAsync($"{scopePrefix}relations");
        await tableService.CreateTableIfNotExistsAsync($"{scopePrefix}index");

        var blobService = new BlobServiceClient(connectionString);
        var containerName = $"kb-{scopePrefix.Substring(2)}"; // strip "kb" prefix for container
        var container = blobService.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.None);
    }

    // ─── Entity CRUD ─────────────────────────────────────────────────────────

    public async Task<KbEntity> AddEntityAsync(
        string connectionString, string scopePrefix,
        string title, string content, string entityType,
        List<string> tags, Dictionary<string, object>? properties,
        string userId)
    {
        var entity = new KbEntity
        {
            Title = title,
            EntityType = entityType,
            Content = content,
            Tags = tags,
            Properties = properties ?? new(),
            Summary = content?.Length > 500 ? content.Substring(0, 500) : content,
            CreatedBy = userId,
            UpdatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Save content to blob
        await SaveEntityContentAsync(connectionString, scopePrefix, entity.Id, content ?? "");

        // Save metadata to table
        var tableEntity = EntityToTableEntity(entity, scopePrefix);
        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");
        await tableClient.CreateIfNotExistsAsync();
        await tableClient.UpsertEntityAsync(tableEntity);

        // Index for search
        await RebuildEntityIndexAsync(connectionString, scopePrefix, entity.Id, title, content ?? "", entityType, tags);

        // Auto-link wiki references
        await ProcessWikiLinksAsync(connectionString, scopePrefix, entity.Id, content ?? "", userId);

        return entity;
    }

    public async Task<KbEntity?> GetEntityAsync(string connectionString, string scopePrefix, string entityId)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");
        try
        {
            // PartitionKey is the entity type — scan by RowKey
            var query = tableClient.QueryAsync<TableEntity>(filter: $"RowKey eq '{entityId}'");
            await foreach (var te in query)
            {
                var entity = TableEntityToEntity(te);
                // Load full content from blob
                entity.Content = await LoadEntityContentAsync(connectionString, scopePrefix, entityId);
                return entity;
            }
            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<KbEntity> UpdateEntityAsync(
        string connectionString, string scopePrefix, string entityId,
        string? title, string? content, string? entityType,
        List<string>? tags, Dictionary<string, object>? properties,
        string userId)
    {
        var existing = await GetEntityAsync(connectionString, scopePrefix, entityId);
        if (existing == null)
            throw new InvalidOperationException($"Entity '{entityId}' not found.");

        // Capture the original PartitionKey (entityType) before any changes.
        // Azure Table Storage uses (PartitionKey=entityType, RowKey=id) as the composite key,
        // so changing entityType writes a NEW row — the old one must be explicitly deleted.
        var oldEntityType = existing.EntityType;

        if (title != null) existing.Title = title;
        if (content != null)
        {
            existing.Content = content;
            existing.Summary = content.Length > 500 ? content.Substring(0, 500) : content;
        }
        if (entityType != null) existing.EntityType = entityType;
        if (tags != null) existing.Tags = tags;
        if (properties != null) existing.Properties = properties;
        existing.UpdatedBy = userId;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        if (content != null)
            await SaveEntityContentAsync(connectionString, scopePrefix, entityId, content);

        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");

        // If entityType changed, delete the stale old row to prevent ghost duplicates.
        // Without this, GetEntityAsync's cross-partition RowKey scan returns whichever row
        // it hits first — potentially returning stale data or causing double-list results.
        if (!string.Equals(oldEntityType, existing.EntityType, StringComparison.OrdinalIgnoreCase))
        {
            try { await tableClient.DeleteEntityAsync(oldEntityType, entityId); } catch { }
        }

        await tableClient.UpsertEntityAsync(EntityToTableEntity(existing, scopePrefix));

        await RebuildEntityIndexAsync(connectionString, scopePrefix, entityId,
            existing.Title, existing.Content ?? "", existing.EntityType, existing.Tags);

        if (content != null)
            await ProcessWikiLinksAsync(connectionString, scopePrefix, entityId, content, userId);

        return existing;
    }

    public async Task DeleteEntityAsync(string connectionString, string scopePrefix, string entityId)
    {
        var entity = await GetEntityAsync(connectionString, scopePrefix, entityId);
        if (entity == null) return;

        // Delete from entities table
        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");
        await tableClient.DeleteEntityAsync(entity.EntityType, entityId);

        // Delete blob content
        await DeleteEntityContentAsync(connectionString, scopePrefix, entityId);

        // Remove from search index
        await RemoveEntityIndexAsync(connectionString, scopePrefix, entityId);

        // Remove all relations involving this entity
        var relations = await GetRelationsAsync(connectionString, scopePrefix, entityId, "both");
        var relTableClient = new TableClient(connectionString, $"{scopePrefix}relations");
        foreach (var rel in relations)
        {
            try { await relTableClient.DeleteEntityAsync(rel.SourceId, $"{rel.RelationType}_{rel.TargetId}"); } catch { }
            if (rel.Bidirectional)
                try { await relTableClient.DeleteEntityAsync(rel.TargetId, $"{rel.RelationType}_{rel.SourceId}"); } catch { }
        }
    }

    public async Task<List<KbEntity>> ListEntitiesAsync(
        string connectionString, string scopePrefix,
        string? entityType = null, string? tag = null, int maxResults = 100)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");
        await tableClient.CreateIfNotExistsAsync();

        var filter = entityType != null ? $"PartitionKey eq '{entityType}'" : null;
        var query = filter != null
            ? tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: maxResults)
            : tableClient.QueryAsync<TableEntity>(maxPerPage: maxResults);

        var results = new List<KbEntity>();
        await foreach (var te in query)
        {
            if (results.Count >= maxResults) break;
            var entity = TableEntityToEntity(te);
            if (tag != null && !entity.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                continue;
            results.Add(entity);
        }
        return results;
    }

    /// <summary>
    /// Paged entity listing using Azure Table Storage continuation tokens.
    /// Returns the items for the requested page and an opaque cursor for the next page.
    /// nextCursor is null when there are no more pages.
    /// NOTE: when a tag filter is applied, cursor is not supported (Azure has no secondary
    /// index on tags). In that case nextCursor is always null and only limit applies.
    /// </summary>
    public async Task<(List<KbEntity> Items, string? NextCursor)> ListEntitiesPagedAsync(
        string connectionString, string scopePrefix,
        string? entityType, string? tag, int limit, string? cursor)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");
        await tableClient.CreateIfNotExistsAsync();

        var filter = entityType != null ? $"PartitionKey eq '{EscapeOData(entityType)}'" : null;

        // Tag filter requires in-memory processing — cursor not supported in this path.
        if (tag != null)
        {
            var scanQuery = filter != null
                ? tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: limit * 5)
                : tableClient.QueryAsync<TableEntity>(maxPerPage: limit * 5);

            var filtered = new List<KbEntity>();
            await foreach (var te in scanQuery)
            {
                if (filtered.Count >= limit) break;
                var e = TableEntityToEntity(te);
                if (e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    filtered.Add(e);
            }
            return (filtered, null);
        }

        // Native Azure cursor pagination
        var pagedQuery = filter != null
            ? tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: limit)
            : tableClient.QueryAsync<TableEntity>(maxPerPage: limit);

        var items = new List<KbEntity>();
        string? nextCursor = null;

        // Decode the incoming cursor from base64url → raw continuation token string
        string? rawContinuationToken = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try { rawContinuationToken = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
            catch { /* invalid cursor — treat as start of list */ }
        }

        await foreach (var page in pagedQuery.AsPages(continuationToken: rawContinuationToken, pageSizeHint: limit))
        {
            foreach (var te in page.Values)
            {
                if (items.Count >= limit) break;
                items.Add(TableEntityToEntity(te));
            }
            // Encode next token to base64url for URL safety
            nextCursor = string.IsNullOrEmpty(page.ContinuationToken)
                ? null
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(page.ContinuationToken));
            break; // one page at a time
        }

        return (items, nextCursor);
    }

    /// <summary>
    /// Paged relation listing. Full cursor pagination is supported only for
    /// direction=outgoing (single partition query). direction=incoming and direction=both
    /// require a cross-partition TargetId scan; for these, limit applies but nextCursor
    /// is always null (single page). The response shape is identical in all cases.
    /// </summary>
    public async Task<(List<KbRelation> Items, string? NextCursor)> GetRelationsPagedAsync(
        string connectionString, string scopePrefix,
        string entityId, string direction, int limit, string? cursor)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}relations");
        await tableClient.CreateIfNotExistsAsync();

        // Outgoing only: single partition query — full cursor support
        if (direction == "outgoing")
        {
            string? rawContinuationToken = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                try { rawContinuationToken = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
                catch { }
            }

            var query = tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{EscapeOData(entityId)}'",
                maxPerPage: limit);

            var items = new List<KbRelation>();
            string? nextCursor = null;

            await foreach (var page in query.AsPages(continuationToken: rawContinuationToken, pageSizeHint: limit))
            {
                foreach (var te in page.Values)
                {
                    if (items.Count >= limit) break;
                    items.Add(TableEntityToRelation(te));
                }
                nextCursor = string.IsNullOrEmpty(page.ContinuationToken)
                    ? null
                    : Convert.ToBase64String(Encoding.UTF8.GetBytes(page.ContinuationToken));
                break;
            }
            return (items, nextCursor);
        }

        // Incoming / both: full TargetId scan — limit only, nextCursor always null
        var results = new List<KbRelation>();

        if (direction == "both")
        {
            // Include outgoing for the 'both' direction
            var outgoing = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{EscapeOData(entityId)}'");
            await foreach (var te in outgoing)
            {
                if (results.Count >= limit) break;
                results.Add(TableEntityToRelation(te));
            }
        }

        if (direction == "incoming" || direction == "both")
        {
            var incoming = tableClient.QueryAsync<TableEntity>(filter: $"TargetId eq '{EscapeOData(entityId)}'");
            await foreach (var te in incoming)
            {
                if (results.Count >= limit) break;
                var rel = TableEntityToRelation(te);
                if (direction == "both" && results.Any(r =>
                    r.SourceId == rel.SourceId && r.TargetId == rel.TargetId && r.RelationType == rel.RelationType))
                    continue;
                results.Add(rel);
            }
        }

        return (results, null);
    }

    // ─── Relations ───────────────────────────────────────────────────────────

    public async Task AddRelationAsync(
        string connectionString, string scopePrefix,
        string sourceId, string targetId, string relationType,
        bool bidirectional, Dictionary<string, object>? properties, string userId)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}relations");
        await tableClient.CreateIfNotExistsAsync();

        // Resolve titles for denormalization
        var sourceEntity = await GetEntityAsync(connectionString, scopePrefix, sourceId);
        var targetEntity = await GetEntityAsync(connectionString, scopePrefix, targetId);

        var relation = new TableEntity(sourceId, $"{relationType}_{targetId}")
        {
            ["SourceId"] = sourceId,
            ["TargetId"] = targetId,
            ["SourceTitle"] = sourceEntity?.Title ?? sourceId,
            ["TargetTitle"] = targetEntity?.Title ?? targetId,
            ["RelationType"] = relationType,
            ["Bidirectional"] = bidirectional,
            ["Properties"] = properties != null ? JsonSerializer.Serialize(properties) : null,
            ["CreatedAt"] = DateTimeOffset.UtcNow,
            ["CreatedBy"] = userId
        };
        await tableClient.UpsertEntityAsync(relation);

        if (bidirectional)
        {
            var reverseRelation = new TableEntity(targetId, $"{relationType}_{sourceId}")
            {
                ["SourceId"] = targetId,
                ["TargetId"] = sourceId,
                ["SourceTitle"] = targetEntity?.Title ?? targetId,
                ["TargetTitle"] = sourceEntity?.Title ?? sourceId,
                ["RelationType"] = relationType,
                ["Bidirectional"] = true,
                ["Properties"] = properties != null ? JsonSerializer.Serialize(properties) : null,
                ["CreatedAt"] = DateTimeOffset.UtcNow,
                ["CreatedBy"] = userId
            };
            await tableClient.UpsertEntityAsync(reverseRelation);
        }
    }

    public async Task RemoveRelationAsync(string connectionString, string scopePrefix,
        string sourceId, string targetId, string relationType)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}relations");
        try { await tableClient.DeleteEntityAsync(sourceId, $"{relationType}_{targetId}"); } catch { }
        try { await tableClient.DeleteEntityAsync(targetId, $"{relationType}_{sourceId}"); } catch { }
    }

    public async Task<List<KbRelation>> GetRelationsAsync(
        string connectionString, string scopePrefix, string entityId, string? direction = "both")
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}relations");
        await tableClient.CreateIfNotExistsAsync();

        var results = new List<KbRelation>();

        // Outgoing: PartitionKey == entityId
        if (direction == "outgoing" || direction == "both")
        {
            var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{entityId}'");
            await foreach (var te in query)
            {
                results.Add(TableEntityToRelation(te));
            }
        }

        // Incoming: TargetId == entityId (requires scan — filter on TargetId column)
        if (direction == "incoming" || direction == "both")
        {
            var incoming = tableClient.QueryAsync<TableEntity>(filter: $"TargetId eq '{entityId}'");
            await foreach (var te in incoming)
            {
                var rel = TableEntityToRelation(te);
                if (direction == "both" && results.Any(r => r.SourceId == rel.SourceId && r.TargetId == rel.TargetId && r.RelationType == rel.RelationType))
                    continue; // skip duplicate if bidirectional already captured
                results.Add(rel);
            }
        }

        return results;
    }

    // ─── Search ──────────────────────────────────────────────────────────────

    public async Task<List<KbSearchResult>> SearchAsync(
        string connectionString, string scopePrefix,
        string query, int maxResults = 20, string? entityType = null)
    {
        var tokens = Tokenize(query.ToLowerInvariant());
        if (!tokens.Any()) return new();

        var scores = new Dictionary<string, (string Title, string EntityType, string MatchedField, int Score)>();
        var tableClient = new TableClient(connectionString, $"{scopePrefix}index");
        await tableClient.CreateIfNotExistsAsync();

        foreach (var token in tokens)
        {
            var hits = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{EscapeOData(token)}'");
            await foreach (var hit in hits)
            {
                var entId = hit.RowKey;
                if (entityType != null && !string.Equals(hit.GetString("EntityType"), entityType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var freq = hit.GetInt32("Frequency") ?? 1;
                var fieldBoost = string.Equals(hit.GetString("FieldName"), "title", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
                var delta = freq * fieldBoost;

                if (scores.TryGetValue(entId, out var existing))
                    scores[entId] = (existing.Title, existing.EntityType, existing.MatchedField, existing.Score + delta);
                else
                    scores[entId] = (
                        hit.GetString("EntityTitle") ?? "",
                        hit.GetString("EntityType") ?? "",
                        hit.GetString("FieldName") ?? "",
                        delta);
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value.Score)
            .Take(maxResults)
            .Select(kv => new KbSearchResult
            {
                EntityId = kv.Key,
                Title = kv.Value.Title,
                EntityType = kv.Value.EntityType,
                MatchedField = kv.Value.MatchedField,
                Score = kv.Value.Score
            })
            .ToList();
    }

    // ─── Graph ───────────────────────────────────────────────────────────────

    public async Task<KbGraph> GetGraphAsync(
        string connectionString, string scopePrefix,
        string? entityType = null, string? tag = null, int maxNodes = 200)
    {
        var entities = await ListEntitiesAsync(connectionString, scopePrefix, entityType, tag, maxNodes);
        var entityIds = entities.Select(e => e.Id).ToHashSet();

        var relTableClient = new TableClient(connectionString, $"{scopePrefix}relations");
        await relTableClient.CreateIfNotExistsAsync();

        var edges = new List<KbGraphEdge>();
        var seenEdges = new HashSet<string>();

        foreach (var entity in entities)
        {
            var relQuery = relTableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{entity.Id}'");
            await foreach (var te in relQuery)
            {
                var targetId = te.GetString("TargetId") ?? "";
                if (!entityIds.Contains(targetId)) continue;
                var edgeKey = $"{entity.Id}_{targetId}_{te.GetString("RelationType")}";
                if (seenEdges.Add(edgeKey))
                {
                    edges.Add(new KbGraphEdge
                    {
                        Source = entity.Id,
                        Target = targetId,
                        Label = te.GetString("RelationType") ?? "related_to",
                        Bidirectional = te.GetBoolean("Bidirectional") ?? false
                    });
                }
            }
        }

        // Calculate connection counts
        var connectionCounts = new Dictionary<string, int>();
        foreach (var edge in edges)
        {
            connectionCounts[edge.Source] = connectionCounts.GetValueOrDefault(edge.Source) + 1;
            connectionCounts[edge.Target] = connectionCounts.GetValueOrDefault(edge.Target) + 1;
        }

        var nodes = entities.Select(e => new KbGraphNode
        {
            Id = e.Id,
            Label = e.Title,
            Type = e.EntityType,
            ConnectionCount = connectionCounts.GetValueOrDefault(e.Id),
            Tags = e.Tags
        }).ToList();

        return new KbGraph { Nodes = nodes, Edges = edges };
    }

    public async Task<KbSubgraph> GetNeighborsAsync(
        string connectionString, string scopePrefix, string entityId, int depth = 2)
    {
        var visitedIds = new HashSet<string>();
        var entities = new List<KbEntity>();
        var edges = new List<KbRelation>();

        await TraverseNeighborsAsync(connectionString, scopePrefix, entityId, depth, visitedIds, entities, edges);

        return new KbSubgraph { Nodes = entities, Edges = edges };
    }

    /// <summary>
    /// Returns a KbGraph (for D3 rendering) limited to the 2-hop neighbourhood
    /// of <paramref name="seedEntityId"/>. Queries far fewer Table Storage rows
    /// than GetGraphAsync which scans up to <c>maxNodes</c> entities globally.
    /// </summary>
    public async Task<KbGraph> GetNeighborhoodGraphAsync(
        string connectionString, string scopePrefix,
        string seedEntityId, int depth = 2)
    {
        var visited  = new HashSet<string>();
        var entities = new List<KbEntity>();
        var relList  = new List<KbRelation>();

        // Outgoing BFS from seed
        await TraverseNeighborsAsync(connectionString, scopePrefix, seedEntityId, depth, visited, entities, relList);

        // Also pull incoming edges to seed (entities that reference the seed)
        // so the neighbourhood is bidirectional without a full table scan.
        var incoming = await GetRelationsAsync(connectionString, scopePrefix, seedEntityId, "incoming");
        foreach (var rel in incoming)
        {
            if (!visited.Contains(rel.SourceId))
            {
                var srcEntity = await GetEntityAsync(connectionString, scopePrefix, rel.SourceId);
                if (srcEntity != null) { entities.Add(srcEntity); visited.Add(rel.SourceId); }
            }
            if (!relList.Any(r => r.SourceId == rel.SourceId && r.TargetId == rel.TargetId && r.RelationType == rel.RelationType))
                relList.Add(rel);
        }

        var entityIds = entities.Select(e => e.Id).ToHashSet();
        var connCounts = new Dictionary<string, int>();

        var graphEdges = relList
            .Where(r => entityIds.Contains(r.SourceId) && entityIds.Contains(r.TargetId))
            .Select(r =>
            {
                connCounts[r.SourceId] = connCounts.GetValueOrDefault(r.SourceId) + 1;
                connCounts[r.TargetId] = connCounts.GetValueOrDefault(r.TargetId) + 1;
                return new KbGraphEdge
                {
                    Source = r.SourceId,
                    Target = r.TargetId,
                    Label  = r.RelationType ?? "related_to",
                    Bidirectional = r.Bidirectional
                };
            }).ToList();

        var graphNodes = entities.Select(e => new KbGraphNode
        {
            Id              = e.Id,
            Label           = e.Title,
            Type            = e.EntityType,
            ConnectionCount = connCounts.GetValueOrDefault(e.Id),
            Tags            = e.Tags
        }).ToList();

        return new KbGraph { Nodes = graphNodes, Edges = graphEdges };
    }

    private async Task TraverseNeighborsAsync(
        string connectionString, string scopePrefix,
        string entityId, int remainingDepth,
        HashSet<string> visited, List<KbEntity> entities, List<KbRelation> edges)
    {
        if (!visited.Add(entityId) || remainingDepth < 0) return;

        var entity = await GetEntityAsync(connectionString, scopePrefix, entityId);
        if (entity != null) entities.Add(entity);

        if (remainingDepth == 0) return;

        var relations = await GetRelationsAsync(connectionString, scopePrefix, entityId, "outgoing");
        foreach (var rel in relations)
        {
            edges.Add(rel);
            await TraverseNeighborsAsync(connectionString, scopePrefix, rel.TargetId, remainingDepth - 1, visited, entities, edges);
        }
    }

    // ─── Indexing ─────────────────────────────────────────────────────────────

    public async Task RebuildEntityIndexAsync(
        string connectionString, string scopePrefix,
        string entityId, string title, string content, string entityType, List<string> tags)
    {
        // Remove old index entries first
        await RemoveEntityIndexAsync(connectionString, scopePrefix, entityId);

        var tableClient = new TableClient(connectionString, $"{scopePrefix}index");
        await tableClient.CreateIfNotExistsAsync();

        var allTerms = new Dictionary<string, (string Field, int Freq)>();

        void AddTerms(string text, string fieldName)
        {
            foreach (var token in Tokenize(text))
            {
                if (allTerms.TryGetValue(token, out var existing))
                    allTerms[token] = (existing.Field, existing.Freq + 1);
                else
                    allTerms[token] = (fieldName, 1);
            }
        }

        AddTerms(title, "title");
        AddTerms(content, "content");
        AddTerms(string.Join(" ", tags), "tags");

        foreach (var (token, (fieldName, freq)) in allTerms)
        {
            var indexEntry = new TableEntity(token, entityId)
            {
                ["EntityTitle"] = title,
                ["EntityType"] = entityType,
                ["FieldName"] = fieldName,
                ["Frequency"] = freq
            };
            try { await tableClient.UpsertEntityAsync(indexEntry); } catch { /* non-fatal */ }
        }
    }

    public async Task RemoveEntityIndexAsync(string connectionString, string scopePrefix, string entityId)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}index");
        await tableClient.CreateIfNotExistsAsync();

        var toDelete = new List<(string Pk, string Rk)>();
        var query = tableClient.QueryAsync<TableEntity>(filter: $"RowKey eq '{entityId}'");
        await foreach (var te in query)
            toDelete.Add((te.PartitionKey, te.RowKey));

        foreach (var (pk, rk) in toDelete)
            try { await tableClient.DeleteEntityAsync(pk, rk); } catch { }
    }

    // ─── Wiki-Link Processing ────────────────────────────────────────────────

    private async Task ProcessWikiLinksAsync(
        string connectionString, string scopePrefix,
        string entityId, string content, string userId)
    {
        var linkPattern = new Regex(@"\[\[([^\]]+)\]\]");
        var linkedTitles = linkPattern.Matches(content)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var title in linkedTitles)
        {
            var target = await FindEntityByTitleAsync(connectionString, scopePrefix, title);
            if (target != null && target.Id != entityId)
            {
                try
                {
                    await AddRelationAsync(connectionString, scopePrefix,
                        entityId, target.Id, "references", bidirectional: false, null, userId);
                }
                catch { /* relation may already exist */ }
            }
        }
    }

    private async Task<KbEntity?> FindEntityByTitleAsync(
        string connectionString, string scopePrefix, string title)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");
        var query = tableClient.QueryAsync<TableEntity>(filter: $"Title eq '{EscapeOData(title)}'");
        await foreach (var te in query)
            return TableEntityToEntity(te);
        return null;
    }

    // ─── Stats ───────────────────────────────────────────────────────────────

    public async Task<int> GetEntityCountAsync(string connectionString, string scopePrefix)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}entities");
        await tableClient.CreateIfNotExistsAsync();
        int count = 0;
        await foreach (var _ in tableClient.QueryAsync<TableEntity>(select: new[] { "RowKey" }))
            count++;
        return count;
    }

    public async Task<int> GetRelationCountAsync(string connectionString, string scopePrefix)
    {
        var tableClient = new TableClient(connectionString, $"{scopePrefix}relations");
        await tableClient.CreateIfNotExistsAsync();
        int count = 0;
        await foreach (var _ in tableClient.QueryAsync<TableEntity>(select: new[] { "RowKey" }))
            count++;
        return count;
    }

    // ─── Bulk Import ─────────────────────────────────────────────────────────

    public async Task<int> BulkImportAsync(
        string connectionString, string scopePrefix,
        string entitiesJson, string userId)
    {
        var items = JsonSerializer.Deserialize<List<KbEntity>>(entitiesJson);
        if (items == null) return 0;

        int count = 0;
        foreach (var item in items)
        {
            await AddEntityAsync(connectionString, scopePrefix,
                item.Title, item.Content ?? "", item.EntityType,
                item.Tags, item.Properties, userId);
            count++;
        }
        return count;
    }

    // ─── Blob Storage Helpers ─────────────────────────────────────────────────

    private string GetContainerName(string scopePrefix)
    {
        // Container name: lowercase alphanumeric + hyphens, 3-63 chars
        var suffix = scopePrefix.StartsWith("kb") ? scopePrefix.Substring(2) : scopePrefix;
        return $"kb-{suffix}".ToLowerInvariant();
    }

    private async Task SaveEntityContentAsync(
        string connectionString, string scopePrefix, string entityId, string content)
    {
        var blobService = new BlobServiceClient(connectionString);
        var container = blobService.GetBlobContainerClient(GetContainerName(scopePrefix));
        await container.CreateIfNotExistsAsync(PublicAccessType.None);
        var blob = container.GetBlobClient($"{entityId}.md");
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new System.IO.MemoryStream(bytes);
        await blob.UploadAsync(stream, overwrite: true);
    }

    private async Task<string?> LoadEntityContentAsync(
        string connectionString, string scopePrefix, string entityId)
    {
        try
        {
            var blobService = new BlobServiceClient(connectionString);
            var container = blobService.GetBlobContainerClient(GetContainerName(scopePrefix));
            var blob = container.GetBlobClient($"{entityId}.md");
            var response = await blob.DownloadContentAsync();
            return response.Value.Content.ToString();
        }
        catch { return null; }
    }

    private async Task DeleteEntityContentAsync(
        string connectionString, string scopePrefix, string entityId)
    {
        try
        {
            var blobService = new BlobServiceClient(connectionString);
            var container = blobService.GetBlobContainerClient(GetContainerName(scopePrefix));
            await container.GetBlobClient($"{entityId}.md").DeleteIfExistsAsync();
        }
        catch { }
    }

    // ─── Table Entity Conversion ──────────────────────────────────────────────

    private static TableEntity EntityToTableEntity(KbEntity entity, string scopePrefix)
    {
        return new TableEntity(entity.EntityType, entity.Id)
        {
            ["Title"] = entity.Title,
            ["Tags"] = string.Join(",", entity.Tags),
            ["CreatedAt"] = entity.CreatedAt,
            ["UpdatedAt"] = entity.UpdatedAt,
            ["CreatedBy"] = entity.CreatedBy,
            ["UpdatedBy"] = entity.UpdatedBy,
            ["BlobPath"] = $"{entity.Id}.md",
            ["Properties"] = JsonSerializer.Serialize(entity.Properties),
            ["Summary"] = entity.Summary
        };
    }

    private static KbEntity TableEntityToEntity(TableEntity te)
    {
        var tagsRaw = te.GetString("Tags") ?? "";
        var tags = tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        Dictionary<string, object> props = new();
        var propsJson = te.GetString("Properties");
        if (!string.IsNullOrEmpty(propsJson))
        {
            try { props = JsonSerializer.Deserialize<Dictionary<string, object>>(propsJson) ?? new(); } catch { }
        }

        return new KbEntity
        {
            Id = te.RowKey,
            EntityType = te.PartitionKey,
            Title = te.GetString("Title") ?? "",
            Tags = tags,
            Properties = props,
            Summary = te.GetString("Summary"),
            CreatedBy = te.GetString("CreatedBy"),
            UpdatedBy = te.GetString("UpdatedBy"),
            CreatedAt = te.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
            UpdatedAt = te.GetDateTimeOffset("UpdatedAt") ?? DateTimeOffset.UtcNow
        };
    }

    private static KbRelation TableEntityToRelation(TableEntity te)
    {
        Dictionary<string, object>? props = null;
        var propsJson = te.GetString("Properties");
        if (!string.IsNullOrEmpty(propsJson))
        {
            try { props = JsonSerializer.Deserialize<Dictionary<string, object>>(propsJson); } catch { }
        }

        return new KbRelation
        {
            SourceId = te.GetString("SourceId") ?? te.PartitionKey,
            TargetId = te.GetString("TargetId") ?? "",
            SourceTitle = te.GetString("SourceTitle"),
            TargetTitle = te.GetString("TargetTitle"),
            RelationType = te.GetString("RelationType") ?? "related_to",
            Bidirectional = te.GetBoolean("Bidirectional") ?? false,
            Properties = props,
            CreatedAt = te.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.UtcNow,
            CreatedBy = te.GetString("CreatedBy")
        };
    }

    // ─── Text Processing Helpers ──────────────────────────────────────────────

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could", "should"
    };

    private static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        return Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct()
            .Take(500) // cap to avoid index explosion
            .ToList();
    }

    private static string EscapeOData(string value)
        => value.Replace("'", "''");
}
