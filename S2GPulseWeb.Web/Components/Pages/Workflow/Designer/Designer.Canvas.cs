using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: Canvas interaction – mouse handlers, pan, zoom, minimap, drag/drop, resize.
/// </summary>
public partial class Designer
{
    private async Task UpdateCanvasOffset()
    {
        try
        {
            var rect = await JSRuntime.InvokeAsync<BoundingClientRect>("eval", 
                $"(function() {{ var el = document.querySelector('.workflow-canvas-container'); if(el) {{ var r = el.getBoundingClientRect(); return {{ left: r.left, top: r.top }}; }} return {{ left: 0, top: 0 }}; }})()");
            canvasOffsetX = rect.Left;
            canvasOffsetY = rect.Top;
        }
        catch
        {
            canvasOffsetX = 0;
            canvasOffsetY = 0;
        }
    }

    private void StartDragFromPalette(string nodeType) => draggedNodeType = nodeType;
    private void HandleDragOver(DragEventArgs e) { }

    private async Task HandleDrop(DragEventArgs e)
    {
        if (!string.IsNullOrEmpty(draggedNodeType))
        {
            // Check node limit (skip if unlimited: MaxNodesPerWorkflow < 0)
            if (currentTierLimits != null && currentTierLimits.MaxNodesPerWorkflow >= 0 && canvasNodes.Count >= currentTierLimits.MaxNodesPerWorkflow)
            {
                workflowNotificationMessage = $"Node limit reached ({currentTierLimits.MaxNodesPerWorkflow} nodes max for your plan). Upgrade to add more nodes.";
                workflowNotificationType = "warning";
                draggedNodeType = null;
                return;
            }
            
            // Check HTTP listener limit (enforced across all user workflows)
            if (draggedNodeType == "HttpListener" && !string.IsNullOrEmpty(currentUserId))
            {
                var (canAdd, currentCount, limit) = await UsageTrackingService.CanAddHttpListenerAsync(currentUserId);
                if (!canAdd)
                {
                    workflowNotificationMessage = $"HTTP Listener limit reached ({currentCount}/{limit} listeners). Upgrade your plan for more listeners.";
                    workflowNotificationType = "warning";
                    draggedNodeType = null;
                    return;
                }
            }
            
            // Check if Scheduler nodes are allowed for this plan
            if (draggedNodeType == "Scheduler" && currentTierLimits != null && !currentTierLimits.CanUseScheduling)
            {
                workflowNotificationMessage = "Scheduling is not available on your current plan. Upgrade to use workflow scheduling.";
                workflowNotificationType = "warning";
                draggedNodeType = null;
                return;
            }
            
            var x = NodeHelper.SnapToGrid(e.OffsetX - 150); // Center the 300px node
            var y = NodeHelper.SnapToGrid(e.OffsetY - 100); // Center the 200px node
            
            var newNode = new CanvasNode
            {
                Id = Guid.NewGuid(),
                NodeType = draggedNodeType,
                Name = NodeHelper.GetDefaultNodeName(draggedNodeType),
                X = x,
                Y = y,
                Width = 300,
                Height = 200,
                Configuration = NodeHelper.GetDefaultConfiguration(draggedNodeType)
            };
            
            // For custom nodes, load the full definition on-demand (for placeholder resolution)
            if (draggedNodeType.StartsWith("Custom_"))
            {
                var existingDef = customNodeDefinitions.FirstOrDefault(d => string.Equals(d.NodeTypeKey, draggedNodeType, StringComparison.OrdinalIgnoreCase));
                if (existingDef == null)
                {
                    var def = await CustomNodeService.GetDefinitionByKeyAsync(draggedNodeType);
                    if (def != null)
                        customNodeDefinitions.Add(def);
                }
            }
            
            canvasNodes.Add(newNode);
            MarkAsChanged();
            draggedNodeType = null;
        }
    }

    private void HandleCanvasMouseDown(MouseEventArgs e)
    {
        selectedNodeId = null;
        
        // Close any open context menus when clicking on canvas
        showContextMenu = false;
        showConnectionContextMenu = false;
        showSurfaceFieldMenu = false;
        showAddSurfaceFieldMenu = false;
        
        // Start panning with left mouse button on empty canvas
        if (e.Button == 0 && draggingNode == null && !isConnecting)
        {
            isPanning = true;
            panStartX = e.ClientX;
            panStartY = e.ClientY;
            panStartPanX = panX;
            panStartPanY = panY;
        }
    }

    private async Task HandleCanvasMouseMove(MouseEventArgs e)
    {
        // Handle panning
        if (isPanning)
        {
            panX = panStartPanX + (e.ClientX - panStartX);
            panY = panStartPanY + (e.ClientY - panStartY);
            
            // Constrain panning to canvas bounds (can't pan beyond the grid)
            ClampPanToCanvasBounds();
            
            StateHasChanged();
            return;
        }
        
        if (draggingNode != null)
        {
            var deltaX = (e.ClientX - dragStartX) / zoomLevel;
            var deltaY = (e.ClientY - dragStartY) / zoomLevel;
            
            if (Math.Abs(deltaX) > DesignerConstants.DragThreshold || Math.Abs(deltaY) > DesignerConstants.DragThreshold)
            {
                wasDragged = true;
            }

            draggingNode.X = NodeHelper.SnapToGrid(nodeStartX + deltaX);
            draggingNode.Y = NodeHelper.SnapToGrid(nodeStartY + deltaY);
            
            draggingNode.X = Math.Max(0, draggingNode.X);
            draggingNode.Y = Math.Max(0, draggingNode.Y);
            
            CheckCanvasBounds(draggingNode.X + draggingNode.Width, draggingNode.Y + draggingNode.Height);
            StateHasChanged();
        }
        else if (resizingNode != null)
        {
            var deltaX = (e.ClientX - dragStartX) / zoomLevel;
            var deltaY = (e.ClientY - dragStartY) / zoomLevel;
            
            // Calculate new dimensions with minimum size constraint
            var newWidth = Math.Max(40, nodeStartWidth + deltaX);
            var newHeight = Math.Max(40, nodeStartHeight + deltaY);
            
            // Snap to grid
            resizingNode.Width = NodeHelper.SnapToGrid(newWidth);
            resizingNode.Height = NodeHelper.SnapToGrid(newHeight);
            
            // Mark that we're resizing (to prevent click-to-open after resize)
            wasResizing = true;
            
            CheckCanvasBounds(resizingNode.X + resizingNode.Width, resizingNode.Y + resizingNode.Height);
            MarkAsChanged();
            StateHasChanged();
        }
        else if (isConnecting && connectingFromNode != null)
        {
            await UpdateCanvasOffset();
            connectingEndX = (e.ClientX - canvasOffsetX - panX) / zoomLevel;
            connectingEndY = (e.ClientY - canvasOffsetY - panY) / zoomLevel;
            StateHasChanged();
        }
    }

    private void CheckCanvasBounds(double x, double y)
    {
        bool changed = false;
        if (x > canvasWidth - 500) { canvasWidth += 1000; changed = true; }
        if (y > canvasHeight - 500) { canvasHeight += 1000; changed = true; }
        if (changed) StateHasChanged();
    }

    private async void HandleCanvasMouseUp(MouseEventArgs e)
    {
        var wasResizingActive = resizingNode != null;
        
        draggingNode = null;
        resizingNode = null;
        isConnecting = false;
        isPanning = false;
        
        // Reset wasResizing after a short delay to allow onclick to check it first
        if (wasResizingActive && wasResizing)
        {
            await Task.Delay(50);
            wasResizing = false;
        }
    }

    private void HandleNodeMouseDown(MouseEventArgs e, CanvasNode node)
    {
        if (e.Button == 0)
        {
            wasDragged = false;
            wasResizing = false; // Reset resize flag on new mouse down
            selectedNodeId = node.Id;
            showContextMenu = false;
            StartDragNode(e, node);
        }
    }

    private void StartDragNode(MouseEventArgs e, CanvasNode node)
    {
        draggingNode = node;
        dragStartX = e.ClientX;
        dragStartY = e.ClientY;
        nodeStartX = node.X;
        nodeStartY = node.Y;
    }

    private void StartResizeNode(MouseEventArgs e, CanvasNode node)
    {
        wasResizing = false; // Reset - will be set true on actual resize movement
        resizingNode = node;
        dragStartX = e.ClientX;
        dragStartY = e.ClientY;
        nodeStartWidth = node.Width;
        nodeStartHeight = node.Height;
    }

    #region Zoom Methods

    private void ZoomIn()
    {
        zoomLevel = Math.Min(DesignerConstants.MaxZoom, zoomLevel + DesignerConstants.ZoomStep);
    }

    private void ZoomOut()
    {
        zoomLevel = Math.Max(DesignerConstants.MinZoom, zoomLevel - DesignerConstants.ZoomStep);
    }

    private void ResetZoom()
    {
        zoomLevel = 1.0;
        panX = 0;
        panY = 0;
    }

    private void OnZoomSliderChange(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var value))
        {
            zoomLevel = value / 100.0;
        }
    }

    /// <summary>
    /// Constrains pan position so the viewport stays within canvas bounds.
    /// </summary>
    private void ClampPanToCanvasBounds()
    {
        // Effective canvas bounds (use max of current canvas size or max allowed)
        var effectiveWidth = Math.Min(canvasWidth, MaxCanvasWidth);
        var effectiveHeight = Math.Min(canvasHeight, MaxCanvasHeight);
        
        // Can't pan so that the left/top edge of canvas goes past the viewport
        panX = Math.Min(0, panX);
        panY = Math.Min(0, panY);
        
        // Can't pan so that the right/bottom edge of canvas is before the viewport
        // This allows showing at least some content
        panX = Math.Max(-(effectiveWidth * zoomLevel - 200), panX);
        panY = Math.Max(-(effectiveHeight * zoomLevel - 200), panY);
    }

    #endregion

    #region Minimap

    private void ToggleMinimap()
    {
        isMinimapCollapsed = !isMinimapCollapsed;
    }

    /// <summary>
    /// Gets the minimap scale factor to fit the canvas into the minimap dimensions.
    /// </summary>
    private double GetMinimapScale()
    {
        var effectiveWidth = Math.Max(Math.Min(canvasWidth, MaxCanvasWidth), 1);
        var effectiveHeight = Math.Max(Math.Min(canvasHeight, MaxCanvasHeight), 1);
        return Math.Min(MinimapWidth / effectiveWidth, MinimapHeight / effectiveHeight);
    }

    /// <summary>
    /// Gets the viewport rectangle position and size for minimap display.
    /// </summary>
    private (double X, double Y, double Width, double Height) GetMinimapViewport()
    {
        var scale = GetMinimapScale();
        // Pan is negative when scrolled, so we invert it for viewport position
        var viewportX = (-panX / zoomLevel) * scale;
        var viewportY = (-panY / zoomLevel) * scale;
        // Viewport size depends on zoom and container size (approximate container to 800x600)
        var viewportWidth = (800 / zoomLevel) * scale;
        var viewportHeight = (600 / zoomLevel) * scale;
        return (viewportX, viewportY, Math.Max(20, viewportWidth), Math.Max(15, viewportHeight));
    }

    private void HandleMinimapClick(MouseEventArgs e)
    {
        if (isMinimapCollapsed) return;
        
        var scale = GetMinimapScale();
        // Calculate where user clicked relative to the minimap canvas area
        // Convert minimap coordinates to canvas coordinates
        var canvasX = e.OffsetX / scale;
        var canvasY = e.OffsetY / scale;
        
        // Center the viewport on the clicked point
        panX = -(canvasX - (400 / zoomLevel)) * zoomLevel;
        panY = -(canvasY - (300 / zoomLevel)) * zoomLevel;
        
        ClampPanToCanvasBounds();
        StateHasChanged();
    }

    private void HandleMinimapMouseDown(MouseEventArgs e)
    {
        if (isMinimapCollapsed) return;
        isMinimapDragging = true;
        HandleMinimapClick(e);
    }

    private void HandleMinimapMouseMove(MouseEventArgs e)
    {
        if (isMinimapDragging)
        {
            HandleMinimapClick(e);
        }
    }

    private void HandleMinimapMouseUp(MouseEventArgs e)
    {
        isMinimapDragging = false;
    }

    #endregion
}
