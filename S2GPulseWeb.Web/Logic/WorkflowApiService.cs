using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service that bridges the API layer to internal workflow operations.
/// Handles auto-layout, auto-labeling, trigger detection, and start/stop.
/// </summary>
public class WorkflowApiService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly WorkflowService _workflowService;
    private readonly WorkflowExecutionService _executionService;
    private readonly ILogger<WorkflowApiService> _logger;

    private const double NodeWidth = 300;
    private const double NodeHeight = 200;
    private const double HorizontalSpacing = 300;
    private const double VerticalSpacing = 200;
    private const double OriginX = 100;
    private const double OriginY = 100;

    // Known connection tag patterns for built-in nodes
    private static readonly Dictionary<string, List<string>> BuiltInNodeTags = new()
    {
        ["Condition"] = new() { "success", "failure" },
        ["Aggregator"] = new() { "valid", "invalid" },
        ["Loop"] = new() { "Loop Array" },
        ["RemoteCommand"] = new() { "run:rm-*" },
    };

    public WorkflowApiService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        WorkflowService workflowService,
        WorkflowExecutionService executionService,
        ILogger<WorkflowApiService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _workflowService = workflowService;
        _executionService = executionService;
        _logger = logger;
    }

    #region CRUD Operations

    public async Task<(WorkflowApiResponse? Result, List<string>? InvalidNodeTypes)> CreateWorkflowAsync(string userId, WorkflowCreateRequest dto)
    {
        // Validate node types before creating
        if (dto.Nodes?.Any() == true)
        {
            var invalid = await ValidateNodeTypesAsync(dto.Nodes.Select(n => n.NodeType).ToList());
            if (invalid.Any())
                return (null, invalid);
            
            // Resolve canonical NodeType keys from DB so stored keys always match exactly
            await NormalizeCustomNodeTypesAsync(dto.Nodes);
        }

        // Determine organization context
        Guid? orgId = await GetActiveOrganizationIdAsync(userId);

        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = userId,
            OrganizationId = orgId
        };

        // Build nodes with auto-layout
        var (nodes, nodeNameToId) = BuildNodes(workflow.Id, dto.Nodes);
        
        // Build connections with auto-labeling
        var (connections, invalidRefs) = BuildConnections(nodes, nodeNameToId, dto.Connections);
        if (invalidRefs.Any())
            return (null, invalidRefs.Select(r => $"connection references unknown node '{r}'").ToList());

        // Apply auto-layout if positions not specified
        AutoLayoutNodes(nodes, connections);

        var saved = await _workflowService.SaveWorkflowAsync(workflow, nodes, connections);

        _logger.LogInformation("API: Created workflow '{Name}' (ID: {Id}) for user {UserId}",
            saved.Name, saved.Id, userId);

        return (await GetWorkflowResponseAsync(saved.Id), null);
    }

    public async Task<(WorkflowApiResponse? Result, List<string>? InvalidNodeTypes)> UpdateWorkflowAsync(string userId, Guid workflowId, WorkflowUpdateRequest dto)
    {
        // Validate node types if nodes are being updated
        if (dto.Nodes?.Any() == true)
        {
            var invalid = await ValidateNodeTypesAsync(dto.Nodes.Select(n => n.NodeType).ToList());
            if (invalid.Any())
                return (null, invalid);
            
            // Resolve canonical NodeType keys from DB so stored keys always match exactly
            await NormalizeCustomNodeTypesAsync(dto.Nodes);
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var existing = await context.Workflows
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);

        if (existing == null) return (null, null);

        existing.Name = dto.Name ?? existing.Name;
        existing.Description = dto.Description ?? existing.Description;

        if (dto.Nodes != null && dto.Connections != null)
        {
            var (nodes, nameToId) = BuildNodes(workflowId, dto.Nodes);
            var (connections, invalidRefs) = BuildConnections(nodes, nameToId, dto.Connections);
            if (invalidRefs.Any())
                return (null, invalidRefs.Select(r => $"connection references unknown node '{r}'").ToList());
            AutoLayoutNodes(nodes, connections);
            await _workflowService.SaveWorkflowAsync(existing, nodes, connections);
        }
        else
        {
            existing.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        return (await GetWorkflowResponseAsync(workflowId), null);
    }

    public async Task<WorkflowApiResponse?> GetWorkflowAsync(string userId, Guid workflowId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .Include(w => w.Nodes).ThenInclude(n => n.OutgoingConnections)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);

        if (workflow == null) return null;
        return MapToResponse(workflow);
    }

    public async Task<List<WorkflowListItem>> ListWorkflowsAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        Guid? orgId = await GetActiveOrganizationIdAsync(userId);

        var query = context.Workflows
            .Include(w => w.Nodes)
            .Where(w => w.OwnerId == userId);

        if (orgId.HasValue)
            query = query.Where(w => w.OrganizationId == orgId);
        else
            query = query.Where(w => w.OrganizationId == null);

        var workflows = await query
            .OrderByDescending(w => w.UpdatedAt)
            .ToListAsync();

        return workflows.Select(w => new WorkflowListItem
        {
            Id = w.Id,
            Name = w.Name,
            Description = w.Description,
            IsActive = w.IsActive,
            NodeCount = w.Nodes.Count,
            CreatedAt = w.CreatedAt,
            UpdatedAt = w.UpdatedAt,
            OrganizationId = w.OrganizationId
        }).ToList();
    }

    public async Task<WorkflowDeletionResult> DeleteWorkflowAsync(string userId, Guid workflowId)
    {
        return await _workflowService.DeleteWorkflowAsync(workflowId, userId);
    }

    #endregion

    #region Node & Connection Sub-Resources

    /// <summary>Add a single node to an existing workflow.</summary>
    public async Task<(WorkflowNodeResponse? Result, List<string>? InvalidNodeTypes)> AddNodeAsync(string userId, Guid workflowId, AddNodeRequest dto)
    {
        // Validate node type exists
        var invalid = await ValidateNodeTypesAsync(new List<string> { dto.NodeType });
        if (invalid.Any())
            return (null, invalid);

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .Include(w => w.Nodes)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);

        if (workflow == null) return (null, null);

        // Auto-position: place to the right of the last node
        double posX = dto.X ?? OriginX;
        double posY = dto.Y ?? OriginY;
        if (dto.X == null && workflow.Nodes.Any())
        {
            posX = workflow.Nodes.Max(n => n.PositionX) + HorizontalSpacing;
            posY = workflow.Nodes.Average(n => n.PositionY);
        }

        var node = new WorkflowNode
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            NodeType = dto.NodeType,
            Name = dto.Name,
            Configuration = dto.Configuration,
            IsTrigger = dto.IsTrigger,
            PositionX = posX,
            PositionY = posY,
            Width = dto.Width ?? NodeWidth,
            Height = dto.Height ?? NodeHeight,
            TagsJson = dto.Tags?.Any() == true ? System.Text.Json.JsonSerializer.Serialize(dto.Tags) : null,
            SurfaceFieldsJson = dto.SurfaceFields?.Any() == true ? System.Text.Json.JsonSerializer.Serialize(dto.SurfaceFields) : null
        };

        context.WorkflowNodes.Add(node);
        workflow.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        return (new WorkflowNodeResponse
        {
            Id = node.Id,
            NodeType = node.NodeType,
            Name = node.Name,
            Configuration = node.Configuration,
            IsTrigger = node.IsTrigger,
            X = node.PositionX,
            Y = node.PositionY,
            Width = node.Width,
            Height = node.Height,
            Tags = string.IsNullOrEmpty(node.TagsJson)
                ? new List<string>()
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(node.TagsJson) ?? new(),
            SurfaceFields = string.IsNullOrEmpty(node.SurfaceFieldsJson)
                ? new List<string>()
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(node.SurfaceFieldsJson) ?? new(),
        }, null);
    }

    /// <summary>Remove a node and all its connections from a workflow.</summary>
    public async Task<bool> RemoveNodeAsync(string userId, Guid workflowId, Guid nodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);
        if (workflow == null) return false;

        var node = await context.WorkflowNodes
            .Include(n => n.OutgoingConnections)
            .Include(n => n.IncomingConnections)
            .FirstOrDefaultAsync(n => n.Id == nodeId && n.WorkflowId == workflowId);
        if (node == null) return false;

        // Remove all connections first
        context.WorkflowConnections.RemoveRange(node.OutgoingConnections);
        context.WorkflowConnections.RemoveRange(node.IncomingConnections);
        context.WorkflowNodes.Remove(node);
        workflow.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>Add a connection between two existing nodes in a workflow.</summary>
    public async Task<WorkflowConnectionResponse?> AddConnectionAsync(string userId, Guid workflowId, AddConnectionRequest dto)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .Include(w => w.Nodes).ThenInclude(n => n.OutgoingConnections)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);
        if (workflow == null) return null;

        var sourceNode = workflow.Nodes.FirstOrDefault(n => n.Id == dto.SourceNodeId);
        var targetNode = workflow.Nodes.FirstOrDefault(n => n.Id == dto.TargetNodeId);
        if (sourceNode == null || targetNode == null) return null;

        // Auto-label inference
        var label = dto.Label;
        if (string.IsNullOrEmpty(label))
        {
            label = InferConnectionLabel(sourceNode.NodeType, sourceNode, dto.TargetNodeId,
                workflow.Nodes.ToList(),
                workflow.Nodes.SelectMany(n => n.OutgoingConnections).ToList());
        }

        var connection = new WorkflowConnection
        {
            Id = Guid.NewGuid(),
            SourceNodeId = dto.SourceNodeId,
            TargetNodeId = dto.TargetNodeId,
            Label = label
        };

        context.WorkflowConnections.Add(connection);
        workflow.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var nodeMap = workflow.Nodes.ToDictionary(n => n.Id, n => n.Name);
        return new WorkflowConnectionResponse
        {
            Id = connection.Id,
            SourceNodeId = connection.SourceNodeId,
            SourceNodeName = nodeMap.GetValueOrDefault(connection.SourceNodeId, ""),
            TargetNodeId = connection.TargetNodeId,
            TargetNodeName = nodeMap.GetValueOrDefault(connection.TargetNodeId, ""),
            Label = connection.Label
        };
    }

    /// <summary>Remove a connection from a workflow.</summary>
    public async Task<bool> RemoveConnectionAsync(string userId, Guid workflowId, Guid connectionId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);
        if (workflow == null) return false;

        var connection = await context.WorkflowConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId &&
                context.WorkflowNodes.Any(n => n.WorkflowId == workflowId &&
                    (n.Id == c.SourceNodeId || n.Id == c.TargetNodeId)));
        if (connection == null) return false;

        context.WorkflowConnections.Remove(connection);
        workflow.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    #endregion

    #region Start / Stop

    public async Task<(bool Success, string? Error)> StartWorkflowAsync(string userId, Guid workflowId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .Include(w => w.Nodes).ThenInclude(n => n.OutgoingConnections)
            .Include(w => w.Nodes).ThenInclude(n => n.IncomingConnections)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);

        if (workflow == null) return (false, "Workflow not found.");

        // Mark as active for auto-start durability
        workflow.IsActive = true;
        await context.SaveChangesAsync();

        // Prepare node/connection data for execution service
        var nodes = workflow.Nodes.Select(n => (
            Id: n.Id,
            NodeType: n.NodeType,
            Name: n.Name,
            Configuration: n.Configuration,
            IsTrigger: n.IsTrigger,
            LoggingSettings: ParseLoggingSettings(n.LoggingSettingsJson)
        )).ToList();

        var connections = workflow.Nodes
            .SelectMany(n => n.OutgoingConnections)
            .Select(c => (
                Id: c.Id,
                SourceId: c.SourceNodeId,
                TargetId: c.TargetNodeId,
                Label: c.Label
            )).ToList();

        return await _executionService.StartWorkflowAsync(
            workflow.Id, workflow.OwnerId, workflow.Name,
            nodes, connections, workflow.OrganizationId);
    }

    public async Task<(bool Success, string? Error)> StopWorkflowAsync(string userId, Guid workflowId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);

        if (workflow == null) return (false, "Workflow not found.");

        workflow.IsActive = false;
        await context.SaveChangesAsync();

        await _executionService.StopWorkflowAsync(workflowId);
        return (true, null);
    }

    #endregion

    #region Auto-Layout

    private static void AutoLayoutNodes(List<WorkflowNode> nodes, List<WorkflowConnection> connections)
    {
        // Only auto-layout nodes that don't have explicit positions
        var needsLayout = nodes.Where(n => n.PositionX == 0 && n.PositionY == 0).ToList();
        if (!needsLayout.Any()) return;

        // Build adjacency for topological sort
        var adjacency = new Dictionary<Guid, List<Guid>>();
        var inDegree = new Dictionary<Guid, int>();

        foreach (var node in needsLayout)
        {
            adjacency[node.Id] = new List<Guid>();
            inDegree[node.Id] = 0;
        }

        foreach (var conn in connections)
        {
            if (adjacency.ContainsKey(conn.SourceNodeId) && inDegree.ContainsKey(conn.TargetNodeId))
            {
                adjacency[conn.SourceNodeId].Add(conn.TargetNodeId);
                inDegree[conn.TargetNodeId]++;
            }
        }

        // Topological sort (Kahn's algorithm) to determine layers
        var queue = new Queue<Guid>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var layers = new List<List<Guid>>();

        while (queue.Any())
        {
            var layer = new List<Guid>();
            var nextQueue = new Queue<Guid>();

            while (queue.Any())
            {
                var nodeId = queue.Dequeue();
                layer.Add(nodeId);

                foreach (var child in adjacency.GetValueOrDefault(nodeId, new()))
                {
                    inDegree[child]--;
                    if (inDegree[child] == 0)
                        nextQueue.Enqueue(child);
                }
            }

            layers.Add(layer);
            queue = nextQueue;
        }

        // Assign positions: top-to-bottom layers, left-to-right siblings
        var nodeMap = needsLayout.ToDictionary(n => n.Id);
        for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++)
        {
            var layer = layers[layerIdx];
            var totalWidth = layer.Count * NodeWidth + (layer.Count - 1) * (HorizontalSpacing - NodeWidth);
            var startX = OriginX + (layer.Count > 1 ? 0 : 0); // Center if single node

            for (int nodeIdx = 0; nodeIdx < layer.Count; nodeIdx++)
            {
                if (nodeMap.TryGetValue(layer[nodeIdx], out var node))
                {
                    node.PositionX = OriginX + nodeIdx * HorizontalSpacing;
                    node.PositionY = OriginY + layerIdx * VerticalSpacing;
                    node.Width = NodeWidth;
                    node.Height = NodeHeight;
                }
            }
        }

        // Handle any disconnected nodes (not in any layer)
        var positioned = layers.SelectMany(l => l).ToHashSet();
        var disconnected = needsLayout.Where(n => !positioned.Contains(n.Id)).ToList();
        for (int i = 0; i < disconnected.Count; i++)
        {
            disconnected[i].PositionX = OriginX + (layers.Max(l => l.Count) + i) * HorizontalSpacing;
            disconnected[i].PositionY = OriginY;
            disconnected[i].Width = NodeWidth;
            disconnected[i].Height = NodeHeight;
        }
    }

    #endregion

    #region Node & Connection Building

    private static (List<WorkflowNode> Nodes, Dictionary<string, Guid> NameToId) BuildNodes(
        Guid workflowId, List<WorkflowNodeDto> dtoNodes)
    {
        var nodes = new List<WorkflowNode>();
        var nameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in dtoNodes)
        {
            var node = new WorkflowNode
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflowId,
                NodeType = dto.NodeType,
                Name = dto.Name,
                Configuration = dto.Configuration,
                IsTrigger = dto.IsTrigger,
                PositionX = dto.X ?? 0,
                PositionY = dto.Y ?? 0,
                Width = dto.Width ?? NodeWidth,
                Height = dto.Height ?? NodeHeight,
                TagsJson = dto.Tags?.Any() == true ? JsonSerializer.Serialize(dto.Tags) : null,
                SurfaceFieldsJson = dto.SurfaceFields?.Any() == true ? JsonSerializer.Serialize(dto.SurfaceFields) : null
            };

            nodes.Add(node);
            nameToId[dto.Name] = node.Id;
        }

        return (nodes, nameToId);
    }

    private static (List<WorkflowConnection> Connections, List<string> InvalidReferences) BuildConnections(
        List<WorkflowNode> nodes,
        Dictionary<string, Guid> nameToId,
        List<WorkflowConnectionDto>? dtoConnections)
    {
        if (dtoConnections == null) return (new List<WorkflowConnection>(), new List<string>());

        var connections = new List<WorkflowConnection>();
        var invalidRefs = new List<string>();
        var nodeMap = nodes.ToDictionary(n => n.Id);

        foreach (var dto in dtoConnections)
        {
            var sourceFound = nameToId.TryGetValue(dto.SourceName, out var sourceId);
            var targetFound = nameToId.TryGetValue(dto.TargetName, out var targetId);

            if (!sourceFound) invalidRefs.Add(dto.SourceName);
            if (!targetFound) invalidRefs.Add(dto.TargetName);
            if (!sourceFound || !targetFound) continue;

            var label = dto.Label;

            // Auto-label: if no label specified, infer from source node's known tags
            if (string.IsNullOrEmpty(label) && nodeMap.TryGetValue(sourceId, out var sourceNode))
            {
                label = InferConnectionLabel(sourceNode.NodeType, sourceNode, targetId, nodes, connections);
            }

            connections.Add(new WorkflowConnection
            {
                Id = Guid.NewGuid(),
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                Label = label
            });
        }

        return (connections, invalidRefs);
    }

    private static string? InferConnectionLabel(string nodeType, WorkflowNode sourceNode,
        Guid targetId, List<WorkflowNode> allNodes, List<WorkflowConnection> existingConns)
    {
        if (!BuiltInNodeTags.TryGetValue(nodeType, out var tags)) return null;

        // For Condition: alternate success/failure based on order
        if (nodeType == "Condition")
        {
            var existingCount = existingConns.Count(c => c.SourceNodeId == sourceNode.Id);
            return existingCount == 0 ? "success" : "failure";
        }

        // For Aggregator: alternate valid/invalid
        if (nodeType == "Aggregator")
        {
            var existingCount = existingConns.Count(c => c.SourceNodeId == sourceNode.Id);
            return existingCount == 0 ? "valid" : "invalid";
        }

        // For agent nodes connecting to orchestrator: infer "agent" label
        var targetNode = allNodes.FirstOrDefault(n => n.Id == targetId);
        if (targetNode?.NodeType == "Orchestrator")
        {
            if (nodeType is "DeepSeekAgent" or "CopilotAgent" or "OpenAI" or "Anthropic" or "Gemini" or "Mistral" or "Groq")
                return "agent";
            if (nodeType == "DeepSeek" && sourceNode.Name.Contains("Steering", StringComparison.OrdinalIgnoreCase))
                return "orchestrate";
        }

        // For StorageClient → StorageTable: "storage"
        if (nodeType == "StorageClient" && targetNode?.NodeType == "StorageTable")
            return "storage";

        return null;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Validates that all provided node types exist in the built-in catalog or custom node definitions.
    /// Returns a list of invalid node type names (empty if all valid).
    /// </summary>
    public async Task<List<string>> ValidateNodeTypesAsync(List<string> nodeTypes)
    {
        if (!nodeTypes.Any()) return new List<string>();

        // Get all valid built-in node type keys
        var builtInTypes = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes()
            .Select(n => n.NodeTypeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Separate out types that need custom node lookup
        var unmatched = nodeTypes.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !builtInTypes.Contains(t))
            .ToList();

        if (!unmatched.Any()) return new List<string>();

        // Check custom node definitions in DB
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var customTypes = await context.CustomNodeDefinitions
            .Where(n => n.IsEnabled)
            .Select(n => n.NodeTypeKey)
            .ToListAsync();

        // Custom nodes can be referenced as "Custom_Key" or just "Key"
        var customSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in customTypes)
        {
            customSet.Add(key);
            customSet.Add($"Custom_{key}");
        }

        return unmatched.Where(t => !customSet.Contains(t)).ToList();
    }

    /// <summary>
    /// Resolves custom node type keys to their canonical DB form.
    /// Ensures stored NodeType matches the DB's NodeTypeKey exactly (correct casing and prefix).
    /// </summary>
    private async Task NormalizeCustomNodeTypesAsync(List<WorkflowNodeDto> nodes)
    {
        // Collect all potential custom node types (non-built-in)
        var builtInTypes = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes()
            .Select(n => n.NodeTypeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var customKeys = nodes
            .Where(n => !builtInTypes.Contains(n.NodeType))
            .Select(n => n.NodeType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!customKeys.Any()) return;

        // Build lookup of all possible forms → canonical key
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var dbKeys = await context.CustomNodeDefinitions
            .Where(n => n.IsEnabled)
            .Select(n => n.NodeTypeKey)
            .ToListAsync();

        // Map: lowered user-input → canonical DB key
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dbKey in dbKeys)
        {
            canonicalMap[dbKey] = dbKey;
            // Also map without Custom_ prefix
            if (dbKey.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
                canonicalMap[dbKey[7..]] = dbKey;
        }

        // Apply canonical keys to DTO nodes
        foreach (var node in nodes)
        {
            if (canonicalMap.TryGetValue(node.NodeType, out var canonical))
                node.NodeType = canonical;
        }
    }

    private async Task<Guid?> GetActiveOrganizationIdAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var pref = await context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);
        return pref?.ActiveOrganizationId;
    }

    private async Task<WorkflowApiResponse> GetWorkflowResponseAsync(Guid workflowId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var workflow = await context.Workflows
            .Include(w => w.Nodes).ThenInclude(n => n.OutgoingConnections)
            .FirstOrDefaultAsync(w => w.Id == workflowId);

        return MapToResponse(workflow!);
    }

    private static WorkflowApiResponse MapToResponse(Workflow workflow)
    {
        var nodeMap = workflow.Nodes.ToDictionary(n => n.Id, n => n.Name);

        return new WorkflowApiResponse
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            IsActive = workflow.IsActive,
            OrganizationId = workflow.OrganizationId,
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
            Nodes = workflow.Nodes.Select(n => new WorkflowNodeResponse
            {
                Id = n.Id,
                NodeType = n.NodeType,
                Name = n.Name,
                Configuration = n.Configuration,
                IsTrigger = n.IsTrigger,
                X = n.PositionX,
                Y = n.PositionY,
                Width = n.Width,
                Height = n.Height,
                Tags = string.IsNullOrEmpty(n.TagsJson)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(n.TagsJson) ?? new(),
                SurfaceFields = string.IsNullOrEmpty(n.SurfaceFieldsJson)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(n.SurfaceFieldsJson) ?? new(),
                LoggingSettings = n.LoggingSettingsJson
            }).ToList(),
            Connections = workflow.Nodes
                .SelectMany(n => n.OutgoingConnections)
                .Select(c => new WorkflowConnectionResponse
                {
                    Id = c.Id,
                    SourceNodeId = c.SourceNodeId,
                    SourceNodeName = nodeMap.GetValueOrDefault(c.SourceNodeId, ""),
                    TargetNodeId = c.TargetNodeId,
                    TargetNodeName = nodeMap.GetValueOrDefault(c.TargetNodeId, ""),
                    Label = c.Label
                }).ToList()
        };
    }

    private static NodeLoggingSettings ParseLoggingSettings(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new NodeLoggingSettings();
        try { return JsonSerializer.Deserialize<NodeLoggingSettings>(json) ?? new NodeLoggingSettings(); }
        catch { return new NodeLoggingSettings(); }
    }

    #endregion
}

#region API DTOs

public class WorkflowCreateRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<WorkflowNodeDto> Nodes { get; set; } = new();
    public List<WorkflowConnectionDto> Connections { get; set; } = new();
}

public class WorkflowUpdateRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<WorkflowNodeDto>? Nodes { get; set; }
    public List<WorkflowConnectionDto>? Connections { get; set; }
}

public class WorkflowNodeDto
{
    public string NodeType { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
    public bool IsTrigger { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? SurfaceFields { get; set; }
}

public class WorkflowConnectionDto
{
    public string SourceName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string? Label { get; set; }
}

public class AddNodeRequest
{
    public string NodeType { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
    public bool IsTrigger { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? SurfaceFields { get; set; }
}

public class AddConnectionRequest
{
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string? Label { get; set; }
}

public class WorkflowApiResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public Guid? OrganizationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<WorkflowNodeResponse> Nodes { get; set; } = new();
    public List<WorkflowConnectionResponse> Connections { get; set; } = new();
}

public class WorkflowNodeResponse
{
    public Guid Id { get; set; }
    public string NodeType { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
    public bool IsTrigger { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> SurfaceFields { get; set; } = new();
    public string? LoggingSettings { get; set; }
}

public class WorkflowConnectionResponse
{
    public Guid Id { get; set; }
    public Guid SourceNodeId { get; set; }
    public string SourceNodeName { get; set; } = "";
    public Guid TargetNodeId { get; set; }
    public string TargetNodeName { get; set; } = "";
    public string? Label { get; set; }
}

public class WorkflowListItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int NodeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? OrganizationId { get; set; }
}

#endregion
