using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;
using S2GPulseWeb.Web.Logic.Nodes;
using System.Text.Json;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: Node operations – selection, delete, context menu, property modal, config changes, viewers.
/// </summary>
public partial class Designer
{
    private void MarkAsChanged()
    {
        hasUnsavedChanges = true;
    }

    private void HandleConfigurationChanged(CanvasNode node, string json)
    {
        node.Configuration = json;
        MarkAsChanged();
        StateHasChanged();
    }

    private void HandleLoggingSettingsChanged(CanvasNode node, NodeLoggingSettings settings)
    {
        node.LoggingSettings = settings;
        MarkAsChanged();
        StateHasChanged();
    }

    private void HandleIconChanged(CanvasNode node, string? icon)
    {
        node.IconOverride = icon;
        MarkAsChanged();
        StateHasChanged();
    }

    private void HandleNodeContextMenu(MouseEventArgs e, CanvasNode node)
    {
        contextMenuX = e.ClientX;
        contextMenuY = e.ClientY;
        contextMenuNode = node;
        showContextMenu = true;
        selectedNodeId = node.Id;
    }

    private void CloseContextMenu() => showContextMenu = false;

    private void ToggleNodeTrigger()
    {
        if (contextMenuNode != null)
        {
            contextMenuNode.IsTrigger = !contextMenuNode.IsTrigger;
            MarkAsChanged();
        }
        showContextMenu = false;
    }

    private void HandleContextDelete()
    {
        if (contextMenuNode != null)
        {
            DeleteNode(contextMenuNode.Id);
        }
    }

    private void SelectNode(CanvasNode node)
    {
        // Single click selects the node
        selectedNodeId = node.Id;
        showContextMenu = false;
        
        // If AI panel is open, add to context (if not already present)
        if (!isAiPanelCollapsed && !aiContextNodes.Any(n => n.Id == node.Id))
        {
            aiContextNodes.Add(node);
        }
        
        StateHasChanged();
    }
    
    private void RemoveFromAiContext(CanvasNode node)
    {
        aiContextNodes.RemoveAll(n => n.Id == node.Id);
        StateHasChanged();
    }
    
    private void ClearAiContext()
    {
        aiContextNodes.Clear();
        StateHasChanged();
    }

    private void OpenPropertyModal(CanvasNode node)
    {
        // Don't open if we were dragging or resizing
        if (wasDragged || wasResizing) return;
        editingNode = node;
        StateHasChanged();
    }

    private void ClosePropertyModal()
    {
        editingNode = null;
        overlayMouseDown = false;
        StateHasChanged();
    }

    private void HandleOverlayMouseDown() => overlayMouseDown = true;
    private void HandleOverlayMouseUp()
    {
        if (overlayMouseDown) ClosePropertyModal();
        overlayMouseDown = false;
    }

    private void DeleteNode(Guid nodeId)
    {
        // Find the node and show confirmation modal
        var node = canvasNodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return;
        
        nodeToDelete = node;
        showNodeDeleteConfirmation = true;
        showContextMenu = false;
    }
    
    private async Task ConfirmDeleteNode()
    {
        if (nodeToDelete == null) return;
        
        var nodeId = nodeToDelete.Id;
        
        canvasNodes.RemoveAll(n => n.Id == nodeId);
        connections.RemoveAll(c => c.SourceId == nodeId || c.TargetId == nodeId);
        if (selectedNodeId == nodeId) selectedNodeId = null;
        MarkAsChanged();
        
        // Also delete logs for this node from the database
        if (!string.IsNullOrEmpty(currentUserId))
        {
            try
            {
                await LogService.ClearLogsAsync(currentUserId, nodeId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete logs for node: {ex.Message}");
            }
        }
        
        showNodeDeleteConfirmation = false;
        nodeToDelete = null;
    }
    
    private void CancelDeleteNode()
    {
        showNodeDeleteConfirmation = false;
        nodeToDelete = null;
    }

    private void UpdateNodeTags(CanvasNode node, string tagsString)
    {
        node.Tags = tagsString
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    /// <summary>
    /// Gets a field value from a custom node's JSON configuration.
    /// </summary>
    private string GetCustomNodeFieldValue(CanvasNode node, string fieldName)
    {
        if (string.IsNullOrEmpty(node.Configuration)) return "";
        try
        {
            var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node.Configuration);
            if (config != null && config.TryGetValue(fieldName, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => value.GetRawText()
                };
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Sets a field value in a custom node's JSON configuration.
    /// </summary>
    private void SetCustomNodeFieldValue(CanvasNode node, string fieldName, string? value)
    {
        try
        {
            var config = string.IsNullOrEmpty(node.Configuration) 
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(node.Configuration) ?? new();
            
            config[fieldName] = value;
            node.Configuration = JsonSerializer.Serialize(config);
            MarkAsChanged();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting custom node field: {ex.Message}");
        }
    }

    private WorkflowNode CreateWorkflowNodeFromCanvas(CanvasNode canvasNode)
    {
        return new WorkflowNode
        {
            Id = canvasNode.Id,
            Name = canvasNode.Name,
            NodeType = canvasNode.NodeType,
            Configuration = canvasNode.Configuration
        };
    }

    private void UpdateNodeStatus(CanvasNode node, ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int status)) node.StatusCode = status;
    }

    private void ShowLogDetail(NodeLogEntry log)
    {
        if (!string.IsNullOrEmpty(log.Detail))
        {
            selectedLogEntry = log;
            StateHasChanged();
        }
    }

    private void CloseLogDetail()
    {
        selectedLogEntry = null;
        StateHasChanged();
    }

    private List<string> GetAvailablePlaceholders(CanvasNode node)
    {
        return PlaceholderHelperInstance.GetAvailablePlaceholders(
            node, canvasNodes, connections, currentWorkflowId, showAllPlaceholders);
    }

    #region Viewer Openers

    private void OpenCacheViewer(Guid nodeId)
    {
        viewingCacheNodeId = nodeId;
        showCacheViewer = true;
        StateHasChanged();
    }

    /// <summary>
    /// Opens the Storage Table data viewer popup for a specific node.
    /// </summary>
    private void OpenStorageTableViewer(CanvasNode node)
    {
        viewingStorageTableNodeId = node.Id;
        showStorageTableViewer = true;
        StateHasChanged();
    }

    /// <summary>
    /// Opens the Storage Table data viewer popup for a specific node ID.
    /// </summary>
    private void OpenStorageTableViewer(Guid nodeId)
    {
        viewingStorageTableNodeId = nodeId;
        showStorageTableViewer = true;
        StateHasChanged();
    }

    /// <summary>
    /// Opens the Remote Machine Monitor popup for a specific node.
    /// </summary>
    private void OpenRemoteMachineMonitor(CanvasNode node)
    {
        viewingRemoteNodeId = node.Id;
        // Extract ClientId from node configuration
        try
        {
            var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node.Configuration ?? "{}");
            if (config != null && config.TryGetValue("ClientId", out var clientIdElement))
            {
                viewingRemoteClientId = clientIdElement.GetString();
            }
        }
        catch { }
        showRemoteMachineMonitor = true;
        StateHasChanged();
    }

    private void OpenVectorDbViewer(Guid nodeId)
    {
        viewingVectorDbNodeId = nodeId;
        showVectorDbViewer = true;
        StateHasChanged();
    }

    private void OpenBlobViewer(Guid nodeId)
    {
        viewingBlobNodeId = nodeId;
        showBlobViewer = true;
        StateHasChanged();
    }

    private void OpenS2GStorageViewer(Guid nodeId)
    {
        viewingS2GStorageNodeId = nodeId;
        showS2GStorageViewer = true;
        StateHasChanged();
    }

    #endregion
}
