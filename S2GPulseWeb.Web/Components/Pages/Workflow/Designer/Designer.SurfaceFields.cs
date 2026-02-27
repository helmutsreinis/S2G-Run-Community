using Microsoft.AspNetCore.Components.Web;
using S2GPulseWeb.Web.Logic;
using S2GPulseWeb.Web.Logic.Nodes;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: Surface field display, resolution, editing, and filtered placeholder support.
/// </summary>
public partial class Designer
{
    #region Surface Field Methods
    
    /// <summary>
    /// Gets the resolved display text for a surface field, resolving any embedded {{placeholders}}.
    /// Returns the field with all placeholders replaced by their values.
    /// Supports nested JSON paths like {{HttpRequest.Body.access_token}}.
    /// </summary>
    private string GetSurfaceFieldDisplay(CanvasNode node, string fieldKey)
    {
        // Use regex to find and replace all {{NodeName.property}} patterns
        var placeholderRegex = new System.Text.RegularExpressions.Regex(@"\{\{([^}]+)\}\}");
        
        var result = placeholderRegex.Replace(fieldKey, match =>
        {
            var innerKey = match.Groups[1].Value; // e.g., "HttpRequest.Body.access_token"
            var parts = innerKey.Split('.', 2);
            if (parts.Length < 2) return match.Value; // Can't resolve, keep original
            
            var nodeName = parts[0];
            var propertyPath = parts[1]; // e.g., "Body.access_token" or just "SchedulerType"
            
            // Try to resolve from the current node
            var resolvedValue = TryResolvePropertyPath(node, nodeName, propertyPath);
            if (resolvedValue != null)
            {
                return resolvedValue.Length > 50 ? resolvedValue.Substring(0, 47) + "..." : resolvedValue;
            }
            
            // Try upstream nodes
            var upstreamNodes = GetUpstreamNodes(node);
            foreach (var upstream in upstreamNodes)
            {
                resolvedValue = TryResolvePropertyPath(upstream, nodeName, propertyPath);
                if (resolvedValue != null)
                {
                    return resolvedValue.Length > 50 ? resolvedValue.Substring(0, 47) + "..." : resolvedValue;
                }
            }
            
            // No value found - show just the last part of the property name as placeholder
            var displayName = propertyPath.Contains('.') ? propertyPath.Split('.').Last() : propertyPath;
            return $"[{displayName}]";
        });
        
        return result;
    }
    
    /// <summary>
    /// Tries to resolve a property path from a node's OutputData.
    /// Supports both direct properties and nested JSON paths (e.g., Body.access_token).
    /// </summary>
    private string? TryResolvePropertyPath(CanvasNode targetNode, string nodeName, string propertyPath)
    {
        if (targetNode.Name != nodeName) return null;
        
        // First try direct property lookup (e.g., "SchedulerType")
        if (targetNode.OutputData.TryGetValue(propertyPath, out var directValue) && directValue != null)
        {
            return directValue.ToString();
        }
        
        // Try nested JSON path (e.g., "Body.access_token")
        var pathParts = propertyPath.Split('.', 2);
        if (pathParts.Length == 2)
        {
            var rootProperty = pathParts[0]; // e.g., "Body"
            var jsonPath = pathParts[1];      // e.g., "access_token"
            
            if (targetNode.OutputData.TryGetValue(rootProperty, out var rootValue) && rootValue != null)
            {
                var jsonString = rootValue.ToString();
                if (!string.IsNullOrEmpty(jsonString))
                {
                    // Try to extract nested JSON value
                    var extracted = ExtractJsonPathValue(jsonString, jsonPath);
                    if (extracted != null) return extracted;
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Extracts a value from a JSON string using a dot-separated path.
    /// </summary>
    private static string? ExtractJsonPathValue(string json, string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var element = doc.RootElement;
            
            foreach (var part in path.Split('.'))
            {
                if (element.ValueKind == System.Text.Json.JsonValueKind.Object && element.TryGetProperty(part, out var child))
                {
                    element = child;
                }
                else
                {
                    return null;
                }
            }
            
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number => element.GetRawText(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                System.Text.Json.JsonValueKind.Null => "",
                _ => element.GetRawText()
            };
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Legacy: Gets the resolved value for a surface field placeholder.
    /// For custom labels (non-placeholder text), returns an empty string (label only).
    /// </summary>
    private string? GetSurfaceFieldValue(CanvasNode node, string placeholderKey)
    {
        // Check if this is a placeholder pattern (starts with {{ and ends with }})
        var isPlaceholder = placeholderKey.StartsWith("{{") && placeholderKey.EndsWith("}}");
        
        if (!isPlaceholder)
        {
            // Custom label - return empty so only the label is shown
            return "";
        }
        
        // Extract the key from {{NodeName.property}} format
        var innerKey = placeholderKey.Trim('{', '}');
        var parts = innerKey.Split('.', 2);
        if (parts.Length < 2) return "";
        
        var nodeName = parts[0];
        var propertyName = parts[1];
        
        // Check if it's this node's own property
        if (nodeName == node.Name && node.OutputData.TryGetValue(propertyName, out var ownValue) && ownValue != null)
        {
            var strValue = ownValue.ToString() ?? "";
            return strValue.Length > 50 ? strValue.Substring(0, 47) + "..." : strValue;
        }
        
        // Check upstream nodes' OutputData
        var upstreamNodes = GetUpstreamNodes(node);
        foreach (var upstream in upstreamNodes)
        {
            if (upstream.Name == nodeName && upstream.OutputData.TryGetValue(propertyName, out var upstreamValue) && upstreamValue != null)
            {
                var strValue = upstreamValue.ToString() ?? "";
                return strValue.Length > 50 ? strValue.Substring(0, 47) + "..." : strValue;
            }
        }
        
        // No runtime value yet - return empty string so placeholder still shows at design time
        return "";
    }
    
    /// <summary>
    /// Gets a human-readable label from a placeholder key.
    /// For custom labels, returns the text as-is.
    /// </summary>
    private string GetSurfaceFieldLabel(string placeholderKey)
    {
        // Check if this is a placeholder pattern
        if (placeholderKey.StartsWith("{{") && placeholderKey.EndsWith("}}"))
        {
            // Extract the property name from "{{NodeName.propertyName}}" format
            var inner = placeholderKey.Trim('{', '}');
            var parts = inner.Split('.');
            return parts.Length > 1 ? parts[^1] : inner;
        }
        
        // Custom label - return as-is
        return placeholderKey;
    }
    
    /// <summary>
    /// Gets upstream nodes that provide data to this node.
    /// </summary>
    private List<CanvasNode> GetUpstreamNodes(CanvasNode node)
    {
        var result = new List<CanvasNode>();
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        
        // Find all nodes connected TO this node
        foreach (var conn in connections.Where(c => c.TargetId == node.Id))
        {
            queue.Enqueue(conn.SourceId);
        }
        
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (visited.Contains(currentId)) continue;
            visited.Add(currentId);
            
            var currentNode = canvasNodes.FirstOrDefault(n => n.Id == currentId);
            if (currentNode != null)
            {
                result.Add(currentNode);
                foreach (var conn in connections.Where(c => c.TargetId == currentId))
                {
                    queue.Enqueue(conn.SourceId);
                }
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Shows the context menu for editing/removing a surface field.
    /// </summary>
    private void ShowRemoveSurfaceFieldMenu(MouseEventArgs e, CanvasNode node, string fieldKey)
    {
        surfaceFieldMenuX = e.ClientX;
        surfaceFieldMenuY = e.ClientY;
        surfaceFieldMenuNode = node;
        surfaceFieldToRemove = fieldKey;
        surfaceFieldEditText = fieldKey; // Pre-populate with current value
        showSurfaceFieldMenu = true;
        showAddSurfaceFieldMenu = false;
        showContextMenu = false;
    }
    
    /// <summary>
    /// Shows the "Add Surface Field" sub-menu from the node context menu.
    /// </summary>
    private void ShowAddSurfaceFieldMenu()
    {
        if (contextMenuNode != null)
        {
            surfaceFieldMenuNode = contextMenuNode;
            surfaceFieldSearchText = "";
            showAddSurfaceFieldMenu = true;
            showSurfaceFieldMenu = false;
        }
    }
    
    /// <summary>
    /// Closes all surface field menus.
    /// </summary>
    private void CloseSurfaceFieldMenu()
    {
        showSurfaceFieldMenu = false;
        showAddSurfaceFieldMenu = false;
        surfaceFieldMenuNode = null;
        surfaceFieldToRemove = null;
        surfaceFieldSearchText = "";
        surfaceFieldEditText = "";
    }
    
    /// <summary>
    /// Saves the edited surface field value.
    /// </summary>
    private void SaveSurfaceFieldEdit()
    {
        if (surfaceFieldMenuNode != null && surfaceFieldToRemove != null && !string.IsNullOrWhiteSpace(surfaceFieldEditText))
        {
            var index = surfaceFieldMenuNode.SurfaceFields.IndexOf(surfaceFieldToRemove);
            if (index >= 0)
            {
                surfaceFieldMenuNode.SurfaceFields[index] = surfaceFieldEditText;
                MarkAsChanged();
            }
        }
        CloseSurfaceFieldMenu();
    }
    
    /// <summary>
    /// Adds a placeholder field to the node's surface display.
    /// </summary>
    private void AddSurfaceField(string placeholderKey)
    {
        if (surfaceFieldMenuNode != null && !surfaceFieldMenuNode.SurfaceFields.Contains(placeholderKey))
        {
            surfaceFieldMenuNode.SurfaceFields.Add(placeholderKey);
            MarkAsChanged();
        }
        CloseSurfaceFieldMenu();
        showContextMenu = false;
    }
    
    /// <summary>
    /// Removes a surface field from the node.
    /// </summary>
    private void RemoveSurfaceField()
    {
        if (surfaceFieldMenuNode != null && surfaceFieldToRemove != null)
        {
            surfaceFieldMenuNode.SurfaceFields.Remove(surfaceFieldToRemove);
            MarkAsChanged();
        }
        CloseSurfaceFieldMenu();
    }
    
    /// <summary>
    /// Gets filtered placeholders for the add surface field menu.
    /// Includes both upstream node placeholders AND the current node's own output parameters.
    /// </summary>
    private List<string> GetFilteredPlaceholders()
    {
        if (surfaceFieldMenuNode == null) return new List<string>();
        
        var allPlaceholders = new List<string>();
        
        // Add upstream node placeholders
        allPlaceholders.AddRange(GetAvailablePlaceholders(surfaceFieldMenuNode));
        
        // Add the current node's OWN output parameters (important for trigger/source nodes)
        var ownParams = NodeHelper.GetOutputParametersForType(surfaceFieldMenuNode.NodeType);
        foreach (var param in ownParams)
        {
            allPlaceholders.Add($"{{{{{surfaceFieldMenuNode.Name}.{param}}}}}");
        }
        
        // For custom nodes, add output parameters from the node definition
        if (NodeHelper.IsCustomNode(surfaceFieldMenuNode.NodeType))
        {
            var customDef = customNodeDefinitions.FirstOrDefault(d => string.Equals(d.NodeTypeKey, surfaceFieldMenuNode.NodeType, StringComparison.OrdinalIgnoreCase));
            if (customDef?.OutputParameters != null)
            {
                foreach (var param in customDef.OutputParameters)
                {
                    allPlaceholders.Add($"{{{{{surfaceFieldMenuNode.Name}.{param.ParameterName}}}}}");
                }
            }
        }
        
        // Also add any keys from this node's OutputData (runtime values)
        foreach (var key in surfaceFieldMenuNode.OutputData.Keys)
        {
            allPlaceholders.Add($"{{{{{surfaceFieldMenuNode.Name}.{key}}}}}");
        }
        
        var existing = surfaceFieldMenuNode.SurfaceFields.ToHashSet();
        
        var filtered = allPlaceholders
            .Distinct()
            .Where(p => !existing.Contains(p))
            .Where(p => string.IsNullOrEmpty(surfaceFieldSearchText) || 
                       p.Contains(surfaceFieldSearchText, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        
        return filtered;
    }
    
    #endregion
}
