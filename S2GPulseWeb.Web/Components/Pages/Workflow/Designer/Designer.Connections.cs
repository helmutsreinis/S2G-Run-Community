using Microsoft.AspNetCore.Components.Web;
using S2GPulseWeb.Web.Data;
using System.Text.Json;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: Connection management – creation, labels, direction resolution, context menus, helpers.
/// </summary>
public partial class Designer
{
    private void HandleNodeMouseUp(MouseEventArgs e, CanvasNode node)
    {
        draggingNode = null;
        if (isConnecting) EndConnection(node);
    }

    private void StartConnectionFromHandle(MouseEventArgs e, CanvasNode node, ConnectionSide side)
    {
        isConnecting = true;
        connectingFromNode = node;
        connectingFromSide = side;
        var port = ConnectionHelper.GetPortPosition(node, side);
        connectingEndX = port.X;
        connectingEndY = port.Y;
    }

    private void EndConnection(CanvasNode targetNode)
    {
        if (isConnecting && connectingFromNode != null && connectingFromNode.Id != targetNode.Id)
        {
            wasDragged = true; // Prevent opening the target node property popup
            if (!connections.Any(c => (c.SourceId == connectingFromNode.Id && c.TargetId == targetNode.Id) || 
                                     (c.SourceId == targetNode.Id && c.TargetId == connectingFromNode.Id)))
            {
                // Determine connection direction and label based on node types
                var (sourceId, targetId, label) = ResolveConnectionDirectionAndLabel(
                    connectingFromNode, targetNode);
                
                // Validate: Max 1 "orchestrate" connection per Orchestrator
                if (label == "orchestrate")
                {
                    var orchestratorId = targetId; // Target is always the Orchestrator after direction resolution
                    var existingOrchestrate = connections.Any(c => 
                        c.Label == "orchestrate" && c.TargetId == orchestratorId);
                    
                    if (existingOrchestrate)
                    {
                        // Show warning - Orchestrator already has a Steering AI
                        workflowNotificationMessage = "This Orchestrator already has a Steering AI connected. Only 1 orchestrate connection is allowed.";
                        workflowNotificationType = "warning";
                        isConnecting = false;
                        connectingFromNode = null;
                        return;
                    }
                }
                
                connections.Add(new NodeConnection
                {
                    Id = Guid.NewGuid(),
                    SourceId = sourceId,
                    TargetId = targetId,
                    Label = label
                });
                MarkAsChanged();
            }
        }
        isConnecting = false;
        connectingFromNode = null;
    }

    /// <summary>
    /// Resolves the correct connection direction and auto-assigns label for special connection types.
    /// Vector Store ↔ Vector Client: Always interpreted as Client → Store with "storage" label.
    /// Storage Table ↔ Storage Client: Always interpreted as Client → Table with "storage" label.
    /// AI Node ↔ Orchestrator: Always interpreted as AI → Orchestrator with "orchestrate" label.
    /// Agent Node ↔ Orchestrator: Always interpreted as Agent → Orchestrator with "agent" label.
    /// </summary>
    private (Guid sourceId, Guid targetId, string? label) ResolveConnectionDirectionAndLabel(
        CanvasNode fromNode, CanvasNode toNode)
    {
        // AI Node ↔ Orchestrator: Always AI → Orchestrator (for Steering AI)
        // Only non-agent AI nodes can be steering AI (DeepSeek, OpenAI, Anthropic, Gemini, Mistral, Groq)
        var steeringAiTypes = new[] { "DeepSeek", "OpenAI", "Anthropic", "Gemini", "Mistral", "Groq" };
        
        if (fromNode.NodeType == "Orchestrator" && steeringAiTypes.Contains(toNode.NodeType))
        {
            // User drew Orchestrator→AI, swap to AI→Orchestrator
            return (toNode.Id, fromNode.Id, "orchestrate");
        }
        if (steeringAiTypes.Contains(fromNode.NodeType) && toNode.NodeType == "Orchestrator")
        {
            // Correct direction, auto-assign label
            return (fromNode.Id, toNode.Id, "orchestrate");
        }
        
        // Agent Node ↔ Orchestrator: Always Agent → Orchestrator with "agent" label
        var agentNodeTypes = new[] { "DeepSeekAgent", "OpenAIAgent", "AnthropicAgent", "GeminiAgent", "MistralAgent", "GroqAgent", "LocalLlmAgent" };
        
        if (fromNode.NodeType == "Orchestrator" && agentNodeTypes.Contains(toNode.NodeType))
        {
            // User drew Orchestrator→Agent, swap to Agent→Orchestrator
            return (toNode.Id, fromNode.Id, "agent");
        }
        if (agentNodeTypes.Contains(fromNode.NodeType) && toNode.NodeType == "Orchestrator")
        {
            // Correct direction, auto-assign label
            return (fromNode.Id, toNode.Id, "agent");
        }
        
        // Vector Store ↔ Vector Client: Always Client → Store
        if (fromNode.NodeType == "VectorDb" && toNode.NodeType == "VectorClient")
        {
            // User drew Store→Client, swap to Client→Store
            return (toNode.Id, fromNode.Id, "storage");
        }
        if (fromNode.NodeType == "VectorClient" && toNode.NodeType == "VectorDb")
        {
            // Correct direction, auto-assign label
            return (fromNode.Id, toNode.Id, "storage");
        }
        
        // Storage Table ↔ Storage Client: Always Client → Table
        if (fromNode.NodeType == "StorageTable" && toNode.NodeType == "StorageClient")
        {
            // User drew Table→Client, swap to Client→Table
            return (toNode.Id, fromNode.Id, "storage");
        }
        if (fromNode.NodeType == "StorageClient" && toNode.NodeType == "StorageTable")
        {
            // Correct direction, auto-assign label
            return (fromNode.Id, toNode.Id, "storage");
        }
        
        // RemoteCommand ↔ Remote: Always RemoteCommand → Remote with auto-incremented "run:rm-XX" label
        if (fromNode.NodeType == "Remote" && toNode.NodeType == "RemoteCommand")
        {
            // User drew Remote→RemoteCommand, swap to RemoteCommand→Remote
            var label = GetNextRunLabel(toNode.Id);
            return (toNode.Id, fromNode.Id, label);
        }
        if (fromNode.NodeType == "RemoteCommand" && toNode.NodeType == "Remote")
        {
            // Correct direction, auto-assign incremental label
            var label = GetNextRunLabel(fromNode.Id);
            return (fromNode.Id, toNode.Id, label);
        }
        
        // Default: keep original direction, no auto-label
        return (fromNode.Id, toNode.Id, null);
    }

    /// <summary>
    /// Gets the next available run:rm-XX label for a RemoteCommand node.
    /// </summary>
    private string GetNextRunLabel(Guid remoteCommandNodeId)
    {
        // Find existing run:rm-* connections from this RemoteCommand node
        var existingLabels = connections
            .Where(c => c.SourceId == remoteCommandNodeId && 
                       c.Label?.StartsWith("run:rm-", StringComparison.OrdinalIgnoreCase) == true)
            .Select(c => c.Label!)
            .ToList();
        
        // Find the highest number used
        int maxNumber = 0;
        foreach (var label in existingLabels)
        {
            var suffix = label.Replace("run:rm-", "");
            if (int.TryParse(suffix, out var num) && num > maxNumber)
            {
                maxNumber = num;
            }
        }
        
        // Return next incremental label
        return $"run:rm-{(maxNumber + 1):D2}";
    }

    private void HandleContextConnect()
    {
        if (contextMenuNode != null)
        {
            isConnecting = true;
            connectingFromNode = contextMenuNode;
            connectingEndX = contextMenuNode.X + contextMenuNode.Width;
            connectingEndY = contextMenuNode.Y + contextMenuNode.Height / 2;
            showContextMenu = false;
        }
    }

    private void HandleConnectionContextMenu(MouseEventArgs e, NodeConnection connection)
    {
        connectionContextMenuX = e.ClientX;
        connectionContextMenuY = e.ClientY;
        contextMenuConnection = connection;
        connectionLabelInput = connection.Label ?? "";
        showConnectionContextMenu = true;
        showContextMenu = false; // Close node context menu if open
    }

    private void CloseConnectionContextMenu() => showConnectionContextMenu = false;

    private void SaveConnectionLabel()
    {
        if (contextMenuConnection != null)
        {
            contextMenuConnection.Label = string.IsNullOrWhiteSpace(connectionLabelInput) ? null : connectionLabelInput.Trim();
            MarkAsChanged();
        }
        showConnectionContextMenu = false;
    }

    private void SetConnectionLabelPreset(string label)
    {
        connectionLabelInput = label;
        SaveConnectionLabel();
    }

    private void DeleteConnection()
    {
        if (contextMenuConnection != null)
        {
            connections.RemoveAll(c => c.Id == contextMenuConnection.Id);
            MarkAsChanged();
        }
        showConnectionContextMenu = false;
    }

    private void DisconnectConnection(Guid sourceId, Guid targetId)
    {
        connections.RemoveAll(c => c.SourceId == sourceId && c.TargetId == targetId);
        MarkAsChanged();
        CloseContextMenu();
        StateHasChanged();
    }

    /// <summary>
    /// Gets the stroke-dasharray value for a connection based on its label type.
    /// </summary>
    private string GetConnectionDashArray(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return ""; // Solid line
            
        var lowerLabel = label.ToLower();
        
        // Agent connections: dashed
        if (lowerLabel == "agent")
            return "8,4";
            
        // Tool connections: double-line effect
        if (lowerLabel.StartsWith("tool:"))
            return "1,0"; // Solid (handled separately with stroke-width)
        
        // Run connections (RemoteCommand → Remote): dash-dot pattern
        if (lowerLabel.StartsWith("run:"))
            return "8,4,2,4";
            
        return lowerLabel switch
        {
            "reader" => "5,5",        // Dotted line
            "storage" => "10,3,2,3",  // Semi-dotted (dash-dot pattern)
            "orchestrate" => "6,3",   // Dashed - bidirectional control flow
            _ => ""                    // Solid line
        };
    }

    /// <summary>
    /// Gets the stroke width for a connection based on its label type.
    /// </summary>
    private int GetConnectionStrokeWidth(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return 2;
            
        // Tool connections: thicker (double-line effect)
        if (label.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
            return 4;
        
        // Orchestrate connections: slightly thicker for visibility
        if (string.Equals(label, "orchestrate", StringComparison.OrdinalIgnoreCase))
            return 3;
        
        // Run connections (RemoteCommand → Remote): slightly thicker
        if (label.StartsWith("run:", StringComparison.OrdinalIgnoreCase))
            return 3;
            
        return 2;
    }

    /// <summary>
    /// Gets the connected Storage Table node ID for a Storage Client node.
    /// Checks both directions: Client→Table (preferred) and Table→Client (fallback).
    /// </summary>
    private Guid? GetConnectedStorageTableId(CanvasNode node)
    {
        // First check: Client → Table (correct direction)
        var storageConnection = connections
            .FirstOrDefault(c => c.SourceId == node.Id && 
                           string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
        
        if (storageConnection != null)
        {
            var targetNode = canvasNodes.FirstOrDefault(n => n.Id == storageConnection.TargetId);
            if (targetNode?.NodeType == "StorageTable")
            {
                return targetNode.Id;
            }
        }
        
        // Fallback: Table → Client (reversed direction)
        storageConnection = connections
            .FirstOrDefault(c => c.TargetId == node.Id && 
                           string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
        
        if (storageConnection != null)
        {
            var sourceNode = canvasNodes.FirstOrDefault(n => n.Id == storageConnection.SourceId);
            if (sourceNode?.NodeType == "StorageTable")
            {
                return sourceNode.Id;
            }
        }
        
        return null;
    }

    /// <summary>
    /// Gets the connected Vector Store node ID for a Vector Client node.
    /// Checks both directions: Client→Store (preferred) and Store→Client (fallback).
    /// </summary>
    private Guid? GetConnectedVectorStoreId(CanvasNode node)
    {
        // First check: Client → Store (correct direction)
        var storageConnection = connections
            .FirstOrDefault(c => c.SourceId == node.Id && 
                           string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
        
        if (storageConnection != null)
        {
            var targetNode = canvasNodes.FirstOrDefault(n => n.Id == storageConnection.TargetId);
            if (targetNode?.NodeType == "VectorDb")
            {
                return targetNode.Id;
            }
        }
        
        // Fallback: Store → Client (reversed direction)
        storageConnection = connections
            .FirstOrDefault(c => c.TargetId == node.Id && 
                           string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
        
        if (storageConnection != null)
        {
            var sourceNode = canvasNodes.FirstOrDefault(n => n.Id == storageConnection.SourceId);
            if (sourceNode?.NodeType == "VectorDb")
            {
                return sourceNode.Id;
            }
        }
        
        return null;
    }

    /// <summary>
    /// Gets column names from the connected Storage Table node's configuration.
    /// </summary>
    private List<string> GetStorageTableColumns(CanvasNode node)
    {
        var columns = new List<string>();
        
        // Find the connected Storage Table
        var storageConnection = connections
            .FirstOrDefault(c => c.SourceId == node.Id && 
                           string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase));
        
        if (storageConnection == null) return columns;
        
        var tableNode = canvasNodes.FirstOrDefault(n => n.Id == storageConnection.TargetId);
        if (tableNode?.NodeType != "StorageTable" || string.IsNullOrEmpty(tableNode.Configuration))
            return columns;
        
        try
        {
            using var doc = JsonDocument.Parse(tableNode.Configuration);
            if (doc.RootElement.TryGetProperty("Columns", out var columnsElement) && 
                columnsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var column in columnsElement.EnumerateArray())
                {
                    if (column.TryGetProperty("Name", out var nameElement))
                    {
                        var columnName = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(columnName))
                        {
                            columns.Add(columnName);
                        }
                    }
                }
            }
        }
        catch
        {
            // Invalid configuration JSON - return empty list
        }
        
        return columns;
    }
}
