using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;
using System.Text.Json;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: Workflow CRUD – save, load, delete, clear, import, export.
/// </summary>
public partial class Designer
{
    private async Task ClearCanvas() 
    { 
        canvasNodes.Clear(); 
        connections.Clear(); 
        selectedNodeId = null; 
        currentWorkflowId = null;
        currentWorkflowName = "New Workflow";
        currentWorkflowIsActive = false;
        currentWorkflowOrganizationId = null;
        canDeleteWorkflow = true; // New workflows can be deleted by creator
        
        // Advance tutorial if waiting on "new-workflow" step
        if (showTutorial && tutorialOverlayRef != null)
            await tutorialOverlayRef.AdvanceIfWaiting("new-workflow");
    }

    private async Task OnWorkflowSelected(ChangeEventArgs e)
    {
        var value = e.Value?.ToString();
        if (string.IsNullOrEmpty(value))
        {
            await ClearCanvas();
            return;
        }
        
        if (Guid.TryParse(value, out var workflowId))
        {
            await LoadWorkflow(workflowId);
        }
    }

    private async Task SaveWorkflow() 
    { 
        if (string.IsNullOrEmpty(currentUserId)) return;

        // Guard: Cannot save while workflow is running
        if (isWorkflowRunning)
        {
            workflowNotificationMessage = "Cannot save while workflow is running. Please stop the workflow first.";
            workflowNotificationType = "danger";
            return;
        }
        
        // Show saving overlay
        isSavingWorkflow = true;
        workflowSavingStatus = "Validating workflow...";
        StateHasChanged();
        await Task.Delay(100); // Small delay to ensure UI updates
        
        try
        {
            // Guard: Check workflow limit for new workflows
            if (!currentWorkflowId.HasValue)
            {
                // Determine which context we're saving to
                var targetOrgId = currentWorkflowOrganizationId ?? activeOrganizationId;
                
                if (targetOrgId.HasValue)
                {
                    // Saving to organization - check org limits (based on founder's plan)
                    var (canCreate, reason) = await OrgUsageTrackingService.CanCreateWorkflowAsync(targetOrgId.Value);
                    if (!canCreate)
                    {
                        workflowNotificationMessage = reason ?? "Cannot create more workflows for this organization.";
                        workflowNotificationType = "warning";
                        return;
                    }
                }
                else
                {
                    // Saving to personal - check personal plan limits
                    var (canCreate, currentCount, limit) = await UsageTrackingService.CanCreateWorkflowAsync(currentUserId);
                    if (!canCreate)
                    {
                        workflowNotificationMessage = $"Workflow limit reached ({limit} workflow{(limit == 1 ? "" : "s")} max for your plan). Upgrade to create more workflows.";
                        workflowNotificationType = "warning";
                        return;
                    }
                }
            }

            // Guard: Check for duplicate node names
            var duplicateNames = canvasNodes
                .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            
            if (duplicateNames.Any())
            {
                var duplicateList = string.Join(", ", duplicateNames.Select(n => $"'{n}'"));
                workflowNotificationMessage = $"Cannot save: Duplicate node names detected: {duplicateList}. Each node must have a unique name for placeholders to work correctly.";
                workflowNotificationType = "danger";
                return;
            }

            workflowSavingStatus = "Preparing workflow data...";
            StateHasChanged();

            var workflow = new Data.Workflow
            {
                Id = currentWorkflowId ?? Guid.NewGuid(),
                Name = currentWorkflowName,
                OwnerId = currentUserId,
                OrganizationId = currentWorkflowOrganizationId ?? activeOrganizationId,
                UpdatedAt = DateTime.UtcNow,
                IsActive = currentWorkflowIsActive
            };

            var nodes = canvasNodes.Select(n => new WorkflowNode
            {
                Id = n.Id,
                WorkflowId = workflow.Id,
                NodeType = n.NodeType,
                Name = n.Name,
                Configuration = n.Configuration,
                PositionX = n.X,
                PositionY = n.Y,
                Width = n.Width,
                Height = n.Height,
                Status = n.Status,
                IsTrigger = n.IsTrigger,
                TagsJson = n.Tags.Any() ? JsonSerializer.Serialize(n.Tags) : null,
                LoggingSettingsJson = JsonSerializer.Serialize(n.LoggingSettings),
                IconOverride = n.IconOverride,
                SurfaceFieldsJson = n.SurfaceFields.Any() ? JsonSerializer.Serialize(n.SurfaceFields) : null
            }).ToList();

            var conns = connections.Select(c => new WorkflowConnection
            {
                Id = c.Id,
                SourceNodeId = c.SourceId,
                TargetNodeId = c.TargetId,
                Label = c.Label
            }).ToList();

            workflowSavingStatus = $"Saving {nodes.Count} nodes...";
            StateHasChanged();

            var saved = await WorkflowService.SaveWorkflowAsync(workflow, nodes, conns);
            currentWorkflowId = saved.Id;
            
            workflowSavingStatus = "Updating workflow list...";
            StateHasChanged();
            
            userWorkflows = await WorkflowService.GetUserWorkflowsAsync(currentUserId, activeOrganizationId);
            
            // Save as last opened workflow
            await PreferenceService.SetLastWorkflowAsync(currentUserId, saved.Id);
            
            // Clear unsaved changes flag and notification
            hasUnsavedChanges = false;
            workflowNotificationMessage = null;
        }
        catch (Exception ex)
        {
            // Error handling - log error
            Console.WriteLine($"Failed to save workflow: {ex.Message}");
            workflowNotificationMessage = $"Failed to save workflow: {ex.Message}";
            workflowNotificationType = "danger";
        }
        finally
        {
            isSavingWorkflow = false;
            StateHasChanged();
        }
    }

    private async Task ShowDeleteConfirmation()
    {
        if (!currentWorkflowId.HasValue || string.IsNullOrEmpty(currentUserId)) return;
        
        deletionInfo = await WorkflowService.GetWorkflowDeletionInfoAsync(currentWorkflowId.Value, currentUserId);
        if (deletionInfo != null)
        {
            showDeleteConfirmation = true;
        }
        else
        {
            workflowNotificationMessage = "Could not load workflow information.";
            workflowNotificationType = "danger";
        }
    }

    private async Task ConfirmDeleteWorkflow()
    {
        if (!currentWorkflowId.HasValue || string.IsNullOrEmpty(currentUserId) || deletionInfo == null) return;
        
        isDeleting = true;
        StateHasChanged();
        
        try
        {
            var result = await WorkflowService.DeleteWorkflowAsync(currentWorkflowId.Value, currentUserId);
            
            if (result.Success)
            {
                // Clear the canvas and reset state
                await ClearCanvas();
                
                // Refresh workflow list
                userWorkflows = await WorkflowService.GetUserWorkflowsAsync(currentUserId, activeOrganizationId);
                
                // Clear last opened workflow preference
                await PreferenceService.SetLastWorkflowAsync(currentUserId, null);
                
                workflowNotificationMessage = $"Workflow \"{result.WorkflowName}\" deleted successfully. Removed {result.NodeCount} nodes, {result.ConnectionCount} connections, and {result.LogsDeleted} logs.";
                workflowNotificationType = "success";
            }
            else
            {
                workflowNotificationMessage = result.ErrorMessage ?? "Failed to delete workflow.";
                workflowNotificationType = "danger";
            }
        }
        catch (Exception ex)
        {
            workflowNotificationMessage = $"Error deleting workflow: {ex.Message}";
            workflowNotificationType = "danger";
        }
        finally
        {
            isDeleting = false;
            showDeleteConfirmation = false;
            deletionInfo = null;
            StateHasChanged();
        }
    }

    private async Task LoadWorkflow(Guid id)
    {
        var workflow = await WorkflowService.GetWorkflowAsync(id);
        if (workflow != null)
        {
            await ClearCanvas();
            currentWorkflowId = workflow.Id;
            currentWorkflowName = workflow.Name;
            currentWorkflowIsActive = workflow.IsActive;
            currentWorkflowOrganizationId = workflow.OrganizationId;
            
            // Determine if user can delete workflow (Contributors cannot delete org workflows)
            if (workflow.OrganizationId.HasValue && !string.IsNullOrEmpty(currentUserId))
            {
                var role = await OrganizationService.GetUserRoleAsync(workflow.OrganizationId.Value, currentUserId);
                canDeleteWorkflow = role >= OrganizationRole.Owner; // Owner or Founder can delete
            }
            else
            {
                canDeleteWorkflow = true; // Personal workflows can always be deleted by owner
            }
            
            foreach (var node in workflow.Nodes)
            {
                var loggingSettings = string.IsNullOrEmpty(node.LoggingSettingsJson)
                    ? new NodeLoggingSettings()
                    : JsonSerializer.Deserialize<NodeLoggingSettings>(node.LoggingSettingsJson) ?? new NodeLoggingSettings();
                
                Console.WriteLine($"[LoadWorkflow] Node {node.Name}: LoggingSettingsJson='{node.LoggingSettingsJson}', Enabled={loggingSettings.LoggingEnabled}");
                
                canvasNodes.Add(new CanvasNode
                {
                    Id = node.Id,
                    NodeType = node.NodeType,
                    Name = node.Name,
                    X = node.PositionX,
                    Y = node.PositionY,
                    Width = node.Width,
                    Height = node.Height,
                    Configuration = node.Configuration,
                    Status = node.Status,
                    IsTrigger = node.IsTrigger,
                    Tags = string.IsNullOrEmpty(node.TagsJson) 
                        ? new List<string>() 
                        : JsonSerializer.Deserialize<List<string>>(node.TagsJson) ?? new List<string>(),
                    LoggingSettings = loggingSettings,
                    IconOverride = node.IconOverride,
                    SurfaceFields = string.IsNullOrEmpty(node.SurfaceFieldsJson)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(node.SurfaceFieldsJson) ?? new List<string>()
                });
            }

            foreach (var node in workflow.Nodes)
            {
                foreach (var conn in node.OutgoingConnections)
                {
                    connections.Add(new NodeConnection
                    {
                        Id = conn.Id,
                        SourceId = conn.SourceNodeId,
                        TargetId = conn.TargetNodeId,
                        Label = conn.Label
                    });
                }
            }
            
            // Pre-load custom node definitions for nodes in this workflow
            var customNodeTypes = workflow.Nodes
                .Where(n => n.NodeType.StartsWith("Custom_"))
                .Select(n => n.NodeType)
                .Distinct()
                .ToList();
            
            if (customNodeTypes.Any())
            {
                isLoadingWorkflowNodes = true;
                workflowLoadingStatus = $"Loading {customNodeTypes.Count} custom node{(customNodeTypes.Count > 1 ? "s" : "")}...";
                StateHasChanged();
                
                try
                {
                    var definitions = await CustomNodeService.GetDefinitionsByKeysAsync(customNodeTypes);
                    foreach (var def in definitions)
                    {
                        if (!customNodeDefinitions.Any(d => d.Id == def.Id))
                            customNodeDefinitions.Add(def);
                    }
                }
                finally
                {
                    isLoadingWorkflowNodes = false;
                    workflowLoadingStatus = "";
                }
            }
            
            showWorkflowList = false;
            hasUnsavedChanges = false; // Loading a saved workflow means no unsaved changes
            workflowNotificationMessage = null;
            StateHasChanged();
        }
    }

    private async Task LoadWorkflowFromData(Data.Workflow workflow)
    {
        currentWorkflowId = workflow.Id;
        currentWorkflowName = workflow.Name;
        currentWorkflowIsActive = workflow.IsActive;
        currentWorkflowOrganizationId = workflow.OrganizationId;
        
        // Determine if user can delete workflow (Contributors cannot delete org workflows)
        if (workflow.OrganizationId.HasValue && !string.IsNullOrEmpty(currentUserId))
        {
            var role = await OrganizationService.GetUserRoleAsync(workflow.OrganizationId.Value, currentUserId);
            canDeleteWorkflow = role >= OrganizationRole.Owner; // Owner or Founder can delete
        }
        else
        {
            canDeleteWorkflow = true; // Personal workflows can always be deleted by owner
        }
        
        canvasNodes.Clear();
        connections.Clear();

        foreach (var node in workflow.Nodes)
        {
            canvasNodes.Add(new CanvasNode
            {
                Id = node.Id,
                NodeType = node.NodeType,
                Name = node.Name,
                X = node.PositionX,
                Y = node.PositionY,
                Width = node.Width,
                Height = node.Height,
                Configuration = node.Configuration,
                Status = node.Status,
                IsTrigger = node.IsTrigger,
                Tags = string.IsNullOrEmpty(node.TagsJson) 
                    ? new List<string>() 
                    : JsonSerializer.Deserialize<List<string>>(node.TagsJson) ?? new List<string>(),
                LoggingSettings = string.IsNullOrEmpty(node.LoggingSettingsJson)
                    ? new NodeLoggingSettings()
                    : JsonSerializer.Deserialize<NodeLoggingSettings>(node.LoggingSettingsJson) ?? new NodeLoggingSettings(),
                IconOverride = node.IconOverride,
                SurfaceFields = string.IsNullOrEmpty(node.SurfaceFieldsJson)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(node.SurfaceFieldsJson) ?? new List<string>()
            });
        }

        foreach (var node in workflow.Nodes)
        {
            foreach (var conn in node.OutgoingConnections)
            {
                connections.Add(new NodeConnection
                {
                    Id = conn.Id,
                    SourceId = conn.SourceNodeId,
                    TargetId = conn.TargetNodeId,
                    Label = conn.Label
                });
            }
        }
        
        // Pre-load custom node definitions for nodes in this workflow
        var customNodeTypes = workflow.Nodes
            .Where(n => n.NodeType.StartsWith("Custom_"))
            .Select(n => n.NodeType)
            .Distinct()
            .ToList();
        
        if (customNodeTypes.Any())
        {
            isLoadingWorkflowNodes = true;
            workflowLoadingStatus = $"Loading {customNodeTypes.Count} custom node{(customNodeTypes.Count > 1 ? "s" : "")}...";
            StateHasChanged();
            
            try
            {
                var definitions = await CustomNodeService.GetDefinitionsByKeysAsync(customNodeTypes);
                foreach (var def in definitions)
                {
                    if (!customNodeDefinitions.Any(d => d.Id == def.Id))
                        customNodeDefinitions.Add(def);
                }
            }
            finally
            {
                isLoadingWorkflowNodes = false;
                workflowLoadingStatus = "";
            }
        }
        
        // Update last opened preference
        if (!string.IsNullOrEmpty(currentUserId))
        {
            await PreferenceService.SetLastWorkflowAsync(currentUserId, workflow.Id);
        }
        
        StateHasChanged();
    }

    private async Task ExportWorkflow()
    {
        // Check tier permission
        if (currentTierLimits != null && !currentTierLimits.CanImportExport)
        {
            workflowNotificationMessage = "Export is not available on your current plan. Upgrade to export workflows.";
            workflowNotificationType = "warning";
            return;
        }
        
        if (!canvasNodes.Any())
        {
            workflowNotificationMessage = "No nodes to export. Please add nodes to the workflow first.";
            workflowNotificationType = "warning";
            return;
        }

        try
        {
            // Build export object
            var exportData = new
            {
                Name = currentWorkflowName,
                ExportedAt = DateTime.UtcNow.ToString("o"),
                Version = "1.0",
                Nodes = canvasNodes.Select(n => new
                {
                    n.Id,
                    n.NodeType,
                    n.Name,
                    n.X,
                    n.Y,
                    n.Width,
                    n.Height,
                    n.Configuration,
                    n.IsTrigger,
                    n.Tags,
                    n.IconOverride,
                    n.SurfaceFields,
                    n.LoggingSettings
                }).ToList(),
                Connections = connections.Select(c => new
                {
                    c.Id,
                    c.SourceId,
                    c.TargetId,
                    c.Label
                }).ToList()
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            // Create safe filename
            var safeName = string.IsNullOrWhiteSpace(currentWorkflowName) 
                ? "workflow" 
                : new string(currentWorkflowName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";

            // Trigger download using JS interop
            await JSRuntime.InvokeVoidAsync("eval", $@"
                (function() {{
                    var blob = new Blob([{JsonSerializer.Serialize(json)}], {{ type: 'application/json' }});
                    var url = URL.createObjectURL(blob);
                    var a = document.createElement('a');
                    a.href = url;
                    a.download = '{fileName}';
                    document.body.appendChild(a);
                    a.click();
                    document.body.removeChild(a);
                    URL.revokeObjectURL(url);
                }})();
            ");

            workflowNotificationMessage = $"Workflow exported successfully as {fileName}";
            workflowNotificationType = "success";
        }
        catch (Exception ex)
        {
            workflowNotificationMessage = $"Failed to export workflow: {ex.Message}";
            workflowNotificationType = "danger";
        }
    }

    private async Task TriggerImportDialog()
    {
        await JSRuntime.InvokeVoidAsync("eval", "document.querySelector('input[type=file][accept=\".json\"]').click();");
    }

    private async Task ImportWorkflow(ChangeEventArgs e)
    {
        // Check tier permission
        if (currentTierLimits != null && !currentTierLimits.CanImportExport)
        {
            workflowNotificationMessage = "Import is not available on your current plan. Upgrade to import workflows.";
            workflowNotificationType = "warning";
            return;
        }
        
        try
        {
            var files = e.Value?.ToString();
            if (string.IsNullOrEmpty(files))
            {
                workflowNotificationMessage = "No file selected.";
                workflowNotificationType = "warning";
                return;
            }

            // Read file content via JS interop
            var fileContent = await JSRuntime.InvokeAsync<string>("eval", @"
                (async function() {
                    var input = document.querySelector('input[type=file][accept="".json""]');
                    if (input && input.files && input.files[0]) {
                        var file = input.files[0];
                        var text = await file.text();
                        input.value = ''; // Reset for next import
                        return text;
                    }
                    return null;
                })()
            ");

            if (string.IsNullOrEmpty(fileContent))
            {
                workflowNotificationMessage = "Failed to read file content.";
                workflowNotificationType = "danger";
                return;
            }

            using var doc = JsonDocument.Parse(fileContent);
            var root = doc.RootElement;

            // Clear current canvas
            canvasNodes.Clear();
            connections.Clear();
            
            // ID mapping: old ID -> new ID (to fix connection references)
            var idMapping = new Dictionary<Guid, Guid>();

            // Load name
            if (root.TryGetProperty("Name", out var nameEl))
            {
                currentWorkflowName = nameEl.GetString() ?? "Imported Workflow";
            }

            // Load nodes - ALWAYS generate new IDs to avoid conflicts
            if (root.TryGetProperty("Nodes", out var nodesEl))
            {
                foreach (var nodeEl in nodesEl.EnumerateArray())
                {
                    // Parse old ID for mapping, but always generate new ID
                    var oldId = nodeEl.TryGetProperty("Id", out var idEl) && Guid.TryParse(idEl.GetString(), out var parsedId) 
                        ? parsedId 
                        : Guid.NewGuid();
                    var newId = Guid.NewGuid();
                    idMapping[oldId] = newId;
                    
                    var node = new CanvasNode
                    {
                        Id = newId,
                        NodeType = nodeEl.TryGetProperty("NodeType", out var typeEl) ? typeEl.GetString() ?? "Process" : "Process",
                        Name = nodeEl.TryGetProperty("Name", out var nameElN) ? nameElN.GetString() ?? "Node" : "Node",
                        X = nodeEl.TryGetProperty("X", out var xEl) ? xEl.GetDouble() : 100,
                        Y = nodeEl.TryGetProperty("Y", out var yEl) ? yEl.GetDouble() : 100,
                        Width = nodeEl.TryGetProperty("Width", out var wEl) ? wEl.GetDouble() : 60,
                        Height = nodeEl.TryGetProperty("Height", out var hEl) ? hEl.GetDouble() : 60,
                        Configuration = nodeEl.TryGetProperty("Configuration", out var configEl) ? configEl.GetString() : null,
                        IsTrigger = nodeEl.TryGetProperty("IsTrigger", out var triggerEl) && triggerEl.GetBoolean(),
                        Tags = new List<string>()
                    };

                    if (nodeEl.TryGetProperty("Tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tag in tagsEl.EnumerateArray())
                        {
                            var tagStr = tag.GetString();
                            if (!string.IsNullOrEmpty(tagStr))
                                node.Tags.Add(tagStr);
                        }
                    }

                    // Load custom icon override if present
                    if (nodeEl.TryGetProperty("IconOverride", out var iconEl))
                    {
                        node.IconOverride = iconEl.GetString();
                    }
                    
                    // Load surface fields if present
                    if (nodeEl.TryGetProperty("SurfaceFields", out var surfaceFieldsEl) && surfaceFieldsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sfEl in surfaceFieldsEl.EnumerateArray())
                        {
                            var sfStr = sfEl.GetString();
                            if (!string.IsNullOrEmpty(sfStr))
                                node.SurfaceFields.Add(sfStr);
                        }
                    }
                    
                    // Load logging settings if present
                    if (nodeEl.TryGetProperty("LoggingSettings", out var loggingEl))
                    {
                        node.LoggingSettings = new NodeLoggingSettings
                        {
                            LoggingEnabled = loggingEl.TryGetProperty("LoggingEnabled", out var leEl) && leEl.GetBoolean(),
                            LogInfo = loggingEl.TryGetProperty("LogInfo", out var liEl) && liEl.GetBoolean(),
                            LogWarning = loggingEl.TryGetProperty("LogWarning", out var lwEl) && lwEl.GetBoolean(),
                            LogError = loggingEl.TryGetProperty("LogError", out var loEl) && loEl.GetBoolean(),
                            LogDebug = loggingEl.TryGetProperty("LogDebug", out var ldEl) && ldEl.GetBoolean()
                        };
                    }

                    canvasNodes.Add(node);
                }
            }

            // Load connections - remap source/target IDs to new node IDs
            if (root.TryGetProperty("Connections", out var connsEl))
            {
                foreach (var connEl in connsEl.EnumerateArray())
                {
                    // Parse old IDs
                    var oldSourceId = connEl.TryGetProperty("SourceId", out var srcEl) && Guid.TryParse(srcEl.GetString(), out var srcId) 
                        ? srcId : Guid.Empty;
                    var oldTargetId = connEl.TryGetProperty("TargetId", out var tgtEl) && Guid.TryParse(tgtEl.GetString(), out var tgtId) 
                        ? tgtId : Guid.Empty;
                    
                    // Map to new IDs
                    var newSourceId = idMapping.TryGetValue(oldSourceId, out var mappedSrc) ? mappedSrc : Guid.Empty;
                    var newTargetId = idMapping.TryGetValue(oldTargetId, out var mappedTgt) ? mappedTgt : Guid.Empty;

                    var conn = new NodeConnection
                    {
                        Id = Guid.NewGuid(), // Always new ID for connections too
                        SourceId = newSourceId,
                        TargetId = newTargetId,
                        Label = connEl.TryGetProperty("Label", out var lblEl) ? lblEl.GetString() : null
                    };

                    if (conn.SourceId != Guid.Empty && conn.TargetId != Guid.Empty)
                    {
                        connections.Add(conn);
                    }
                }
            }

            currentWorkflowId = null; // New workflow (not saved yet)
            hasUnsavedChanges = true;
            workflowNotificationMessage = $"Workflow '{currentWorkflowName}' imported successfully with {canvasNodes.Count} nodes.";
            workflowNotificationType = "success";
            StateHasChanged();
        }
        catch (Exception ex)
        {
            workflowNotificationMessage = $"Failed to import workflow: {ex.Message}";
            workflowNotificationType = "danger";
        }
    }
}
