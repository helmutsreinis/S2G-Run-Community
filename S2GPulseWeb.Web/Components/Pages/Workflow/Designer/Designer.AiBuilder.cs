using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;
using System.Text.Json;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: AI Builder panel – chat, providers, actions, autocomplete, markdown rendering.
/// </summary>
public partial class Designer
{
    private async Task SendAiMessage()
    {
        if (string.IsNullOrWhiteSpace(aiInputMessage) || isAiThinking) return;
        var message = aiInputMessage;
        
        // Build context for selected nodes (sent to AI but not displayed to user)
        var contextJson = BuildAiContextNodesJson();
        
        // Build tier constraints info for AI
        var tierConstraints = currentTierLimits != null ? new
        {
            maxNodesPerWorkflow = currentTierLimits.MaxNodesPerWorkflow,
            currentNodeCount = canvasNodes.Count,
            remainingNodes = currentTierLimits.MaxNodesPerWorkflow - canvasNodes.Count,
            canUseScheduling = currentTierLimits.CanUseScheduling
        } : null;
        var tierConstraintsJson = tierConstraints != null ? JsonSerializer.Serialize(tierConstraints) : null;
        
        aiInputMessage = "";
        isAiThinking = true;
        StateHasChanged();
        
        // Pass provider, mode, model, context, temperature, tier constraints, and Copilot connection to the assistant service
        var result = await AssistantService.SendMessageAsync(
            message, 
            currentUserId ?? "", 
            selectedAiProvider,
            aiChatMode,
            selectedAiModel,
            contextJson,
            aiTemperatureMode,
            tierConstraintsJson,
            copilotConnectionId);
        
        if (result.Success && result.Actions.Any()) 
            await ApplyAiActionsAsync(result.Actions);
        
        isAiThinking = false;
        StateHasChanged();
        await JSRuntime.InvokeVoidAsync("eval", "var el = document.querySelector('.ai-messages'); if(el) el.scrollTop = el.scrollHeight;");
    }

    private void HandleAiKey(KeyboardEventArgs e) { if (e.Key == "Enter" && !e.ShiftKey) _ = SendAiMessage(); }

    private async Task ApplyAiActionsAsync(List<WorkflowAction> actions)
    {
        foreach (var action in actions)
        {
            var actionType = action.Action?.ToLower() ?? "";
            
            if (actionType == "create_node")
            {
                var type = action.Parameters["type"]?.ToString() ?? "Process";
                var name = action.Parameters["name"]?.ToString() ?? "New Node";
                var xPos = double.Parse(action.Parameters["x"]?.ToString() ?? "100");
                var yPos = double.Parse(action.Parameters["y"]?.ToString() ?? "100");
                
                // Parse width/height with sensible defaults
                var width = double.Parse(action.Parameters.TryGetValue("width", out var w) ? w?.ToString() ?? "300" : "300");
                var height = double.Parse(action.Parameters.TryGetValue("height", out var h) ? h?.ToString() ?? "200" : "200");
                
                // Ensure minimum dimensions (hard limit: 300x100)
                if (width < 300) width = 300;
                if (height < 100) height = 200;
                
                // Start with default configuration
                var config = NodeHelper.GetDefaultConfiguration(type);
                
                // Merge any properties specified by AI
                if (action.Parameters.TryGetValue("properties", out var propsObj) && propsObj != null)
                {
                    try
                    {
                        var defaultConfig = string.IsNullOrEmpty(config) 
                            ? new Dictionary<string, object>() 
                            : JsonSerializer.Deserialize<Dictionary<string, object>>(config) ?? new();
                        
                        // Parse AI-provided properties
                        var propsJson = propsObj is JsonElement je ? je.GetRawText() : propsObj.ToString();
                        var aiProps = JsonSerializer.Deserialize<Dictionary<string, object>>(propsJson ?? "{}") ?? new();
                        
                        foreach (var kv in aiProps)
                        {
                            defaultConfig[kv.Key] = kv.Value?.ToString() ?? "";
                        }
                        
                        config = JsonSerializer.Serialize(defaultConfig);
                    }
                    catch { /* Ignore parse errors, use default config */ }
                }
                
                canvasNodes.Add(new CanvasNode 
                { 
                    Id = Guid.NewGuid(), 
                    NodeType = type, 
                    Name = name, 
                    X = NodeHelper.SnapToGrid(xPos), 
                    Y = NodeHelper.SnapToGrid(yPos), 
                    Width = width, 
                    Height = height, 
                    Configuration = config 
                });
                
                // For custom nodes, load the full definition on-demand (same as drop)
                if (type.StartsWith("Custom_"))
                {
                    var existingDef = customNodeDefinitions.FirstOrDefault(d => string.Equals(d.NodeTypeKey, type, StringComparison.OrdinalIgnoreCase));
                    if (existingDef == null)
                    {
                        var def = await CustomNodeService.GetDefinitionByKeyAsync(type);
                        if (def != null)
                            customNodeDefinitions.Add(def);
                    }
                }
            }
            else if (actionType == "connect_nodes")
            {
                var sourceName = action.Parameters["sourceNodeName"]?.ToString();
                var targetName = action.Parameters["targetNodeName"]?.ToString();
                var label = action.Parameters.TryGetValue("label", out var labelVal) ? labelVal?.ToString() : null;
                
                var sourceNode = canvasNodes.FirstOrDefault(n => 
                    n.Name?.Equals(sourceName, StringComparison.OrdinalIgnoreCase) == true);
                var targetNode = canvasNodes.FirstOrDefault(n => 
                    n.Name?.Equals(targetName, StringComparison.OrdinalIgnoreCase) == true);
                
                if (sourceNode != null && targetNode != null)
                {
                    connections.Add(new NodeConnection
                    {
                        Id = Guid.NewGuid(),
                        SourceId = sourceNode.Id,
                        TargetId = targetNode.Id,
                        Label = label
                    });
                }
            }
            else if (actionType == "set_property")
            {
                var nodeName = action.Parameters["nodeName"]?.ToString();
                var propertyName = action.Parameters["propertyName"]?.ToString();
                var value = action.Parameters["value"]?.ToString();
                
                var node = canvasNodes.FirstOrDefault(n => 
                    n.Name?.Equals(nodeName, StringComparison.OrdinalIgnoreCase) == true);
                
                if (node != null && !string.IsNullOrEmpty(propertyName))
                {
                    try
                    {
                        var config = string.IsNullOrEmpty(node.Configuration) 
                            ? new Dictionary<string, object>() 
                            : JsonSerializer.Deserialize<Dictionary<string, object>>(node.Configuration) ?? new();
                        config[propertyName] = value ?? "";
                        node.Configuration = JsonSerializer.Serialize(config);
                    }
                    catch { /* Ignore parse errors */ }
                }
            }
            else if (actionType == "clear_workflow")
            {
                canvasNodes.Clear();
                connections.Clear();
            }
            else if (actionType == "set_surface_fields")
            {
                var nodeName = action.Parameters["nodeName"]?.ToString();
                var surfaceFieldsArray = action.Parameters.TryGetValue("surfaceFields", out var fields) 
                    ? fields as JsonElement? 
                    : null;
                
                var node = canvasNodes.FirstOrDefault(n => 
                    n.Name?.Equals(nodeName, StringComparison.OrdinalIgnoreCase) == true);
                
                if (node != null && surfaceFieldsArray.HasValue)
                {
                    node.SurfaceFields.Clear();
                    foreach (var field in surfaceFieldsArray.Value.EnumerateArray())
                    {
                        var fieldStr = field.GetString();
                        if (!string.IsNullOrEmpty(fieldStr))
                        {
                            node.SurfaceFields.Add(fieldStr);
                        }
                    }
                }
            }
            else if (actionType == "move_node")
            {
                var nodeName = action.Parameters["nodeName"]?.ToString();
                var xPos = double.Parse(action.Parameters["x"]?.ToString() ?? "100");
                var yPos = double.Parse(action.Parameters["y"]?.ToString() ?? "100");
                
                var node = canvasNodes.FirstOrDefault(n => 
                    n.Name?.Equals(nodeName, StringComparison.OrdinalIgnoreCase) == true);
                
                if (node != null)
                {
                    node.X = NodeHelper.SnapToGrid(xPos);
                    node.Y = NodeHelper.SnapToGrid(yPos);
                }
            }
            else if (actionType == "resize_node")
            {
                var nodeName = action.Parameters["nodeName"]?.ToString();
                var width = double.Parse(action.Parameters["width"]?.ToString() ?? "200");
                var height = double.Parse(action.Parameters["height"]?.ToString() ?? "100");
                
                // Ensure minimum dimensions (hard limit: 300x100)
                if (width < 300) width = 300;
                if (height < 100) height = 100;
                
                var node = canvasNodes.FirstOrDefault(n => 
                    n.Name?.Equals(nodeName, StringComparison.OrdinalIgnoreCase) == true);
                
                if (node != null)
                {
                    node.Width = width;
                    node.Height = height;
                }
            }
        }
        
        hasUnsavedChanges = true;
        StateHasChanged();
    }

    #region AI Builder Helpers
    
    /// <summary>
    /// Loads AI provider information including API key status.
    /// Respects organization context: in org mode, checks org secrets/connections.
    /// </summary>
    private async Task LoadAiProvidersAsync()
    {
        if (string.IsNullOrEmpty(currentUserId)) return;
        
        var providerSecrets = new[]
        {
            ("OpenAI", "OpenAI_ApiKey"),
            ("DeepSeek", "DeepSeek_ApiKey"),
            ("Anthropic", "Anthropic_ApiKey"),
            ("Gemini", "Gemini_ApiKey"),
            ("Mistral", "Mistral_ApiKey"),
            ("Groq", "Groq_ApiKey")
        };
        
        aiProviders.Clear();
        foreach (var (name, secretKey) in providerSecrets)
        {
            // Use org secrets in org context, personal secrets otherwise
            var key = await SecretService.GetSecretAsync(currentUserId, secretKey, activeOrganizationId);
            aiProviders.Add(new AiProviderInfo(name, !string.IsNullOrEmpty(key)));
        }
        
        // Add Copilot provider (uses OAuth connection instead of API key)
        // In org context, only show org Copilot connections
        var copilotConnections = await CopilotService.GetCopilotConnectionsAsync(currentUserId, activeOrganizationId);
        bool hasCopilotConnection = copilotConnections.Any();
        
        if (hasCopilotConnection)
        {
            // Use first available connection in context
            var connection = copilotConnections.First();
            copilotConnectionId = connection.Id;
            copilotConnectionEmail = connection.Email ?? connection.ConnectionName;
        }
        else
        {
            copilotConnectionId = null;
            copilotConnectionEmail = null;
        }
        
        aiProviders.Add(new AiProviderInfo("Copilot", hasCopilotConnection));
        
        // Default to first provider with a key, or OpenAI
        var firstWithKey = aiProviders.FirstOrDefault(p => p.HasKey);
        if (firstWithKey != null)
        {
            selectedAiProvider = firstWithKey.Name;
        }
        
        // Set available models for selected provider
        UpdateAvailableModels();
    }
    
    /// <summary>
    /// Updates the available models when provider changes
    /// </summary>
    private void UpdateAvailableModels()
    {
        availableModels = WorkflowAssistantService.GetModelsForProvider(selectedAiProvider);
        selectedAiModel = WorkflowAssistantService.GetDefaultModel(selectedAiProvider);
    }
    
    /// <summary>
    /// Handles provider selection change
    /// </summary>
    private void OnProviderChanged(ChangeEventArgs e)
    {
        selectedAiProvider = e.Value?.ToString() ?? "OpenAI";
        UpdateAvailableModels();
    }
    
    /// <summary>
    /// Builds a JSON context for context-selected nodes including their configs and connections
    /// </summary>
    private string BuildAiContextNodesJson()
    {
        if (!aiContextNodes.Any()) return "";
        
        var contextData = aiContextNodes.Select(node => new
        {
            name = node.Name,
            type = node.NodeType,
            configuration = node.Configuration,
            incomingConnections = connections
                .Where(c => c.TargetId == node.Id)
                .Select(c => new { 
                    fromNode = canvasNodes.FirstOrDefault(n => n.Id == c.SourceId)?.Name ?? "Unknown",
                    label = c.Label 
                }),
            outgoingConnections = connections
                .Where(c => c.SourceId == node.Id)
                .Select(c => new { 
                    toNode = canvasNodes.FirstOrDefault(n => n.Id == c.TargetId)?.Name ?? "Unknown",
                    label = c.Label 
                })
        });
        
        return JsonSerializer.Serialize(new
        {
            selectedNodesForContext = contextData
        }, new JsonSerializerOptions { WriteIndented = true });
    }
    
    /// <summary>
    /// Gets the SVG icon for a node for display in AI context list
    /// </summary>
    private string GetNodeIconSvg(CanvasNode node)
    {
        // Check if it's a custom node with SVG
        var customDef = customNodeDefinitions.FirstOrDefault(d => string.Equals(d.NodeTypeKey, node.NodeType, StringComparison.OrdinalIgnoreCase));
        if (customDef != null && !string.IsNullOrEmpty(customDef.IconSvg))
        {
            return customDef.IconSvg;
        }
        // Use standard icon
        return NodeHelper.GetDisplayIcon(node.NodeType, null);
    }
    
    /// <summary>
    /// Handles AI input changes for node reference autocomplete detection
    /// </summary>
    private void HandleAiInputChanged(ChangeEventArgs e)
    {
        var text = e.Value?.ToString() ?? "";
        aiInputMessage = text;
        
        // Detect {{ pattern for node autocomplete
        var lastBraces = text.LastIndexOf("{{");
        var lastCloseBraces = text.LastIndexOf("}}");
        
        if (lastBraces >= 0 && (lastCloseBraces < lastBraces || lastCloseBraces == -1))
        {
            var partial = text.Substring(lastBraces + 2);
            nodeAutocompleteMatches = GetNodeAutocompleteSuggestions(partial);
            showNodeAutocomplete = nodeAutocompleteMatches.Any();
            selectedAutocompleteIndex = 0;
        }
        else
        {
            showNodeAutocomplete = false;
        }
    }
    
    /// <summary>
    /// Gets node name suggestions for autocomplete
    /// </summary>
    private List<string> GetNodeAutocompleteSuggestions(string partial)
    {
        var matches = new List<string>();
        var partialLower = partial.ToLower();
        
        if (aiChatMode == "Build")
        {
            // Build mode: Show generic placeholders
            var genericNames = new[] { "Listener", "Response", "Request", "Query", "Condition", "Result", "Data" };
            matches.AddRange(genericNames
                .Where(n => string.IsNullOrEmpty(partial) || n.ToLower().Contains(partialLower))
                .Take(5));
        }
        else
        {
            // Ask/Edit mode: Show actual canvas nodes
            matches.AddRange(canvasNodes
                .Where(n => !string.IsNullOrEmpty(n.Name) && 
                           (string.IsNullOrEmpty(partial) || n.Name.ToLower().Contains(partialLower)))
                .OrderBy(n => n.Name)
                .Take(5)
                .Select(n => n.Name));
        }
        
        return matches;
    }
    
    /// <summary>
    /// Inserts a node reference at the current position
    /// </summary>
    private void InsertNodeReference(string nodeName)
    {
        var lastBraces = aiInputMessage.LastIndexOf("{{");
        if (lastBraces >= 0)
        {
            aiInputMessage = aiInputMessage.Substring(0, lastBraces) + "{{" + nodeName + "}}";
        }
        showNodeAutocomplete = false;
    }
    
    /// <summary>
    /// Handles keyboard events for autocomplete navigation
    /// </summary>
    private void HandleAiInputKeyDown(KeyboardEventArgs e)
    {
        if (!showNodeAutocomplete) return;
        
        if (e.Key == "Tab" && nodeAutocompleteMatches.Any())
        {
            InsertNodeReference(nodeAutocompleteMatches[selectedAutocompleteIndex]);
        }
        else if (e.Key == "ArrowDown")
        {
            selectedAutocompleteIndex = Math.Min(selectedAutocompleteIndex + 1, nodeAutocompleteMatches.Count - 1);
        }
        else if (e.Key == "ArrowUp")
        {
            selectedAutocompleteIndex = Math.Max(selectedAutocompleteIndex - 1, 0);
        }
        else if (e.Key == "Escape")
        {
            showNodeAutocomplete = false;
        }
    }
    
    /// <summary>
    /// Gets placeholder text for AI input based on current mode
    /// </summary>
    private string GetAiPlaceholder()
    {
        return aiChatMode switch
        {
            "Ask" => "Ask about workflows, nodes, or best practices...",
            "Build" => "Describe the workflow you want to create...",
            "Edit" => "Describe changes to make (use {{NodeName}} to reference nodes)...",
            _ => "Type your message..."
        };
    }
    
    // AI Panel Drag Handlers
    private void HandleAiPanelDragStart(MouseEventArgs e)
    {
        isDraggingAiPanel = true;
        aiPanelDragStartX = e.ClientX - aiPanelX;
        aiPanelDragStartY = e.ClientY - aiPanelY;
    }
    
    private void HandleAiPanelDragMove(MouseEventArgs e)
    {
        if (!isDraggingAiPanel) return;
        
        aiPanelX = e.ClientX - aiPanelDragStartX;
        aiPanelY = e.ClientY - aiPanelDragStartY;
        
        // Keep within viewport bounds
        if (aiPanelX < 0) aiPanelX = 0;
        if (aiPanelY < 60) aiPanelY = 60; // Below nav
        
        StateHasChanged();
    }
    
    private void HandleAiPanelDragEnd(MouseEventArgs e)
    {
        isDraggingAiPanel = false;
    }
    
    /// <summary>
    /// Renders markdown content to HTML for chat display
    /// </summary>
    private string RenderMarkdown(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        
        var html = content;
        
        // Escape HTML first
        html = System.Web.HttpUtility.HtmlEncode(html);
        
        // Convert \n to actual line breaks (from JSON)
        html = html.Replace("\\n", "\n");
        
        // Code blocks (```...```)
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"```(\w*)\n?([\s\S]*?)```", 
            "<pre><code class=\"language-$1\">$2</code></pre>");
        
        // Inline code (`...`)
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"`([^`]+)`", 
            "<code>$1</code>");
        
        // Bold (**...**)
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"\*\*([^*]+)\*\*", 
            "<strong>$1</strong>");
        
        // Italic (*...*)
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"\*([^*]+)\*", 
            "<em>$1</em>");
        
        // Headers (### ...)
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"^### (.+)$", 
            "<h4>$1</h4>", 
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"^## (.+)$", 
            "<h3>$1</h3>", 
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Bullet lists (- ...)
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"^- (.+)$", 
            "<li>$1</li>", 
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Wrap consecutive <li> in <ul>
        html = System.Text.RegularExpressions.Regex.Replace(
            html, 
            @"(<li>.*?</li>\n?)+", 
            "<ul>$0</ul>");
        
        // Line breaks
        html = html.Replace("\n\n", "</p><p>");
        html = html.Replace("\n", "<br/>");
        
        // Wrap in paragraph if not already wrapped
        if (!html.StartsWith("<"))
        {
            html = $"<p>{html}</p>";
        }
        
        return html;
    }
    
    #endregion
}

/// <summary>
/// AI Provider information including API key availability
/// </summary>
public record AiProviderInfo(string Name, bool HasKey);
