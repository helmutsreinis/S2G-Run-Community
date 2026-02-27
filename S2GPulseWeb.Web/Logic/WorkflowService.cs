using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace S2GPulseWeb.Web.Logic;

public class WorkflowService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly CacheStorageService _cacheStorageService;

    public WorkflowService(IDbContextFactory<ApplicationDbContext> dbContextFactory, CacheStorageService cacheStorageService)
    {
        _dbContextFactory = dbContextFactory;
        _cacheStorageService = cacheStorageService;
    }

    /// <summary>
    /// Get workflows for a user, filtered by organization context.
    /// When organizationId is null, returns personal workflows only.
    /// When organizationId is set, returns that organization's workflows (user must be a member).
    /// </summary>
    public async Task<List<Workflow>> GetUserWorkflowsAsync(string userId, Guid? organizationId = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        if (organizationId.HasValue)
        {
            // Organization context: verify membership and get org workflows
            var isMember = await context.OrganizationMembers
                .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId);
            
            if (!isMember)
                return new List<Workflow>();
            
            return await context.Workflows
                .Include(w => w.Nodes)
                .Where(w => w.OrganizationId == organizationId)
                .OrderByDescending(w => w.UpdatedAt)
                .ToListAsync();
        }
        else
        {
            // Personal context: get user's personal workflows only (not assigned to any org)
            return await context.Workflows
                .Include(w => w.Nodes)
                .Where(w => w.OwnerId == userId && w.OrganizationId == null)
                .OrderByDescending(w => w.UpdatedAt)
                .ToListAsync();
        }
    }

    public async Task<Workflow?> GetWorkflowAsync(Guid id)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Workflows
            .Include(w => w.Nodes)
                .ThenInclude(n => n.OutgoingConnections)
            .Include(w => w.Nodes)
                .ThenInclude(n => n.IncomingConnections)
            .FirstOrDefaultAsync(w => w.Id == id);
    }

    public async Task<Workflow> SaveWorkflowAsync(Workflow workflow, List<WorkflowNode> nodes, List<WorkflowConnection> connections)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var existingWorkflow = await context.Workflows
            .Include(w => w.Nodes)
            .ThenInclude(n => n.OutgoingConnections)
            .Include(w => w.Nodes)
            .ThenInclude(n => n.IncomingConnections)
            .FirstOrDefaultAsync(w => w.Id == workflow.Id);

        if (existingWorkflow == null)
        {
            workflow.CreatedAt = DateTime.UtcNow;
            workflow.UpdatedAt = DateTime.UtcNow;
            context.Workflows.Add(workflow);
            existingWorkflow = workflow;
        }
        else
        {
            existingWorkflow.Name = workflow.Name;
            existingWorkflow.Description = workflow.Description;
            existingWorkflow.UpdatedAt = DateTime.UtcNow;
            existingWorkflow.IsActive = workflow.IsActive;
        }

        // Remove connections that are no longer present
        var existingConnections = existingWorkflow.Nodes.SelectMany(n => n.OutgoingConnections).ToList();
        foreach (var conn in existingConnections)
        {
            if (!connections.Any(c => c.Id == conn.Id))
            {
                context.WorkflowConnections.Remove(conn);
            }
        }

        // Remove nodes that are no longer present
        foreach (var node in existingWorkflow.Nodes.ToList())
        {
            if (!nodes.Any(n => n.Id == node.Id))
            {
                context.WorkflowNodes.Remove(node);
            }
        }

        // Add or update nodes
        foreach (var node in nodes)
        {
            var existingNode = existingWorkflow.Nodes.FirstOrDefault(n => n.Id == node.Id);
            if (existingNode == null)
            {
                node.WorkflowId = existingWorkflow.Id;
                context.WorkflowNodes.Add(node);
            }
            else
            {
                existingNode.Name = node.Name;
                existingNode.NodeType = node.NodeType;
                existingNode.Configuration = node.Configuration;
                existingNode.PositionX = node.PositionX;
                existingNode.PositionY = node.PositionY;
                existingNode.Width = node.Width;
                existingNode.Height = node.Height;
                existingNode.Status = node.Status;
                existingNode.TagsJson = node.TagsJson;
                existingNode.IsTrigger = node.IsTrigger;
                existingNode.LoggingSettingsJson = node.LoggingSettingsJson;
                existingNode.IconOverride = node.IconOverride;
                existingNode.SurfaceFieldsJson = node.SurfaceFieldsJson;
            }
        }

        // Add or update connections
        foreach (var conn in connections)
        {
            var existingConn = await context.WorkflowConnections.FirstOrDefaultAsync(c => c.Id == conn.Id);
            if (existingConn == null)
            {
                context.WorkflowConnections.Add(conn);
            }
            else
            {
                existingConn.SourceNodeId = conn.SourceNodeId;
                existingConn.TargetNodeId = conn.TargetNodeId;
                existingConn.Label = conn.Label;
            }
        }

        await context.SaveChangesAsync();
        return existingWorkflow;
    }

    /// <summary>
    /// Delete a workflow and all associated data (nodes, connections, logs, storage data, vectors).
    /// Returns a summary of what was deleted.
    /// For organization workflows, only Owner/Founder can delete (not Contributors).
    /// </summary>
    public async Task<WorkflowDeletionResult> DeleteWorkflowAsync(Guid workflowId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // First, fetch the workflow by ID only to check ownership and org membership
        var workflow = await context.Workflows
            .Include(w => w.Nodes)
            .ThenInclude(n => n.OutgoingConnections)
            .Include(w => w.Nodes)
            .ThenInclude(n => n.IncomingConnections)
            .FirstOrDefaultAsync(w => w.Id == workflowId);
        
        if (workflow == null)
        {
            return new WorkflowDeletionResult { Success = false, ErrorMessage = "Workflow not found." };
        }
        
        // Authorization check
        bool canDelete = false;
        
        if (workflow.OrganizationId.HasValue)
        {
            // Organization workflow: only Owner or Founder can delete (check role directly to avoid circular dependency)
            var member = await context.OrganizationMembers
                .FirstOrDefaultAsync(m => m.OrganizationId == workflow.OrganizationId.Value && m.UserId == userId);
            canDelete = member != null && member.Role >= OrganizationRole.Owner;
        }
        else
        {
            // Personal workflow: only owner can delete
            canDelete = workflow.OwnerId == userId;
        }
        
        if (!canDelete)
        {
            return new WorkflowDeletionResult { Success = false, ErrorMessage = "You don't have permission to delete this workflow." };
        }
        
        var result = new WorkflowDeletionResult
        {
            Success = true,
            WorkflowName = workflow.Name,
            NodeCount = workflow.Nodes.Count,
            ConnectionCount = workflow.Nodes.SelectMany(n => n.OutgoingConnections).Count()
        };
        
        // Get all node IDs for associated data deletion
        var nodeIds = workflow.Nodes.Select(n => n.Id).ToList();
        
        // Delete logs for all nodes in this workflow
        var logsToDelete = await context.NodeLogs
            .Where(l => l.WorkflowId == workflowId || nodeIds.Contains(l.NodeId))
            .ToListAsync();
        result.LogsDeleted = logsToDelete.Count;
        context.NodeLogs.RemoveRange(logsToDelete);
        
        // Delete VectorDocuments for any VectorDB nodes in this workflow
        var vectorDocsToDelete = await context.VectorDocuments
            .Where(v => nodeIds.Contains(v.VectorDbNodeId))
            .ToListAsync();
        result.VectorDocsDeleted = vectorDocsToDelete.Count;
        context.VectorDocuments.RemoveRange(vectorDocsToDelete);
        
        // Delete StorageTableRecords for any StorageTable nodes in this workflow
        var storageRecordsToDelete = await context.StorageTableRecords
            .Where(r => nodeIds.Contains(r.StorageTableNodeId))
            .ToListAsync();
        result.StorageRecordsDeleted = storageRecordsToDelete.Count;
        context.StorageTableRecords.RemoveRange(storageRecordsToDelete);
        
        // Delete StorageTableColumns for any StorageTable nodes in this workflow
        var storageColumnsToDelete = await context.StorageTableColumns
            .Where(c => nodeIds.Contains(c.StorageTableNodeId))
            .ToListAsync();
        result.StorageColumnsDeleted = storageColumnsToDelete.Count;
        context.StorageTableColumns.RemoveRange(storageColumnsToDelete);
        
        // Clear in-memory cache for this workflow
        _cacheStorageService.ClearWorkflow(workflowId);
        
        // Explicitly delete connections first (they use Restrict delete behavior, not Cascade)
        var allConnections = workflow.Nodes
            .SelectMany(n => n.OutgoingConnections)
            .Concat(workflow.Nodes.SelectMany(n => n.IncomingConnections))
            .Distinct()
            .ToList();
        context.WorkflowConnections.RemoveRange(allConnections);
        
        // Delete the workflow (cascade deletes nodes via EF)
        context.Workflows.Remove(workflow);
        
        await context.SaveChangesAsync();
        
        return result;
    }

    /// <summary>
    /// Get workflow info for deletion confirmation.
    /// </summary>
    public async Task<WorkflowDeletionInfo?> GetWorkflowDeletionInfoAsync(Guid workflowId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var workflow = await context.Workflows
            .Include(w => w.Nodes)
            .ThenInclude(n => n.OutgoingConnections)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);
        
        if (workflow == null) return null;
        
        var nodeIds = workflow.Nodes.Select(n => n.Id).ToList();
        
        var logCount = await context.NodeLogs
            .Where(l => l.WorkflowId == workflowId || nodeIds.Contains(l.NodeId))
            .CountAsync();
        
        var vectorDocCount = await context.VectorDocuments
            .Where(v => nodeIds.Contains(v.VectorDbNodeId))
            .CountAsync();
        
        var storageRecordCount = await context.StorageTableRecords
            .Where(r => nodeIds.Contains(r.StorageTableNodeId))
            .CountAsync();
        
        return new WorkflowDeletionInfo
        {
            WorkflowId = workflowId,
            WorkflowName = workflow.Name,
            NodeCount = workflow.Nodes.Count,
            ConnectionCount = workflow.Nodes.SelectMany(n => n.OutgoingConnections).Count(),
            LogCount = logCount,
            VectorDocCount = vectorDocCount,
            StorageRecordCount = storageRecordCount
        };
    }
}

public class WorkflowDeletionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string WorkflowName { get; set; } = "";
    public int NodeCount { get; set; }
    public int ConnectionCount { get; set; }
    public int LogsDeleted { get; set; }
    public int VectorDocsDeleted { get; set; }
    public int StorageRecordsDeleted { get; set; }
    public int StorageColumnsDeleted { get; set; }
}

public class WorkflowDeletionInfo
{
    public Guid WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public int NodeCount { get; set; }
    public int ConnectionCount { get; set; }
    public int LogCount { get; set; }
    public int VectorDocCount { get; set; }
    public int StorageRecordCount { get; set; }
}
