using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;
using System.Text.Json;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Partial class: Catalog browsing, custom node filtering, cost panel calculations.
/// </summary>
public partial class Designer
{
    private void ToggleCatalogCategory(Guid categoryId)
    {
        // Toggle category expansion - no heavy loading, uses lightweight catalog
        if (expandedCatalogCategories.Contains(categoryId))
        {
            expandedCatalogCategories.Remove(categoryId);
        }
        else
        {
            expandedCatalogCategories.Add(categoryId);
        }
    }
    
    private bool IsCatalogCategoryExpanded(Guid categoryId) => expandedCatalogCategories.Contains(categoryId);

    /// <summary>
    /// Called when search text changes - loads all nodes into cache if needed.
    /// </summary>
    private async Task OnSearchTextChanged()
    {
        if (!string.IsNullOrWhiteSpace(customNodeSearchText))
        {
            isSearchLoading = true;
            try
            {
                // Ensure all nodes are loaded for search to work properly
                await CustomNodeService.EnsureAllNodesLoadedAsync();
            }
            finally
            {
                isSearchLoading = false;
            }
        }
    }

    /// <summary>
    /// Gets custom node categories filtered by search text.
    /// When searching, limits to top 5 matching nodes total across all categories.
    /// </summary>
    private List<CustomNodeCategory> GetFilteredCustomNodes()
    {
        var hasSearch = !string.IsNullOrWhiteSpace(customNodeSearchText);
        
        if (!hasSearch)
        {
            // Return all enabled categories (nodes load lazily when expanded)
            return customNodeCategories
                .Where(c => c.IsEnabled)
                .ToList();
        }
        
        // Filter by search text - search in category name, node display name, and description
        var searchLower = customNodeSearchText.ToLower();
        var matchingNodes = new List<(CustomNodeCategory Category, CustomNodeDefinition Node)>();
        var categoriesWithBuiltInMatches = new HashSet<Guid>();
        var categoriesMatchingByName = new HashSet<Guid>();
        
        foreach (var category in customNodeCategories.Where(c => c.IsEnabled))
        {
            // Check if category name matches
            var categoryMatches = category.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase);
            if (categoryMatches)
                categoriesMatchingByName.Add(category.Id);
            
            // Check cached custom nodes from global cache
            var loadedNodes = CustomNodeService.GetCachedNodesForCategory(category.Id);
            foreach (var node in loadedNodes)
            {
                var nodeMatches = node.DisplayName.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                                 (node.Description?.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ?? false);
                
                if (categoryMatches || nodeMatches)
                {
                    matchingNodes.Add((category, node));
                }
            }
            
            // Check built-in nodes assigned to this category
            var assignedBuiltInNodes = BuiltInNodeCatalogService.GetAssignedNodes(category.Id);
            if (assignedBuiltInNodes.Any())
            {
                var allBuiltInNodes = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes();
                var hasMatchingBuiltIn = allBuiltInNodes.Any(n => 
                    assignedBuiltInNodes.Contains(n.NodeTypeKey) && (
                        categoryMatches ||
                        n.DisplayName.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                        n.NodeTypeKey.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                        (n.Description?.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ?? false)
                    ));
                
                if (hasMatchingBuiltIn)
                {
                    categoriesWithBuiltInMatches.Add(category.Id);
                }
            }
        }
        
        // Build result - categories with matching custom nodes
        var result = matchingNodes
            .GroupBy(x => x.Category)
            .Select(g => new CustomNodeCategory
            {
                Id = g.Key.Id,
                Name = g.Key.Name,
                Description = g.Key.Description,
                IconEmoji = g.Key.IconEmoji,
                IconSvg = g.Key.IconSvg,
                DisplayOrder = g.Key.DisplayOrder,
                IsEnabled = g.Key.IsEnabled,
                Nodes = g.Select(x => x.Node).ToList()
            })
            .ToList();
        
        // Add categories that match by name or have matching built-in nodes (not already in result)
        var existingCategoryIds = result.Select(c => c.Id).ToHashSet();
        var categoriesToAdd = categoriesWithBuiltInMatches.Union(categoriesMatchingByName)
            .Where(id => !existingCategoryIds.Contains(id));
        
        foreach (var categoryId in categoriesToAdd)
        {
            var originalCategory = customNodeCategories.FirstOrDefault(c => c.Id == categoryId);
            if (originalCategory != null)
            {
                result.Add(new CustomNodeCategory
                {
                    Id = originalCategory.Id,
                    Name = originalCategory.Name,
                    Description = originalCategory.Description,
                    IconEmoji = originalCategory.IconEmoji,
                    IconSvg = originalCategory.IconSvg,
                    DisplayOrder = originalCategory.DisplayOrder,
                    IsEnabled = originalCategory.IsEnabled,
                    Nodes = new List<CustomNodeDefinition>()
                });
            }
        }
        
        return result.OrderBy(c => c.DisplayOrder).Take(10).ToList();
    }

    /// <summary>
    /// Gets built-in nodes assigned to a category from the catalog config.
    /// </summary>
    private List<BuiltInNodeInfo> GetBuiltInNodesForCategory(Guid categoryId)
    {
        var assignedNodeTypes = BuiltInNodeCatalogService.GetAssignedNodes(categoryId);
        if (!assignedNodeTypes.Any())
            return new List<BuiltInNodeInfo>();
        
        var allBuiltInNodes = BuiltInNodeCatalogService.GetAllBuiltInNodeTypes();
        var result = allBuiltInNodes.Where(n => assignedNodeTypes.Contains(n.NodeTypeKey)).ToList();
        
        // Apply search filter if active
        if (!string.IsNullOrWhiteSpace(customNodeSearchText))
        {
            var searchLower = customNodeSearchText.ToLower();
            result = result.Where(n => 
                n.DisplayName.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                n.NodeTypeKey.Contains(searchLower, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
        
        return result;
    }

    /// <summary>
    /// Gets lightweight catalog items for a category.
    /// Uses the pre-loaded catalog cache - no database queries.
    /// </summary>
    private List<CustomNodeCatalogItem> GetCatalogItemsForCategory(Guid categoryId)
    {
        return customNodeCatalogItems.Where(c => c.CategoryId == categoryId).ToList();
    }

    #region Cost Panel

    private double CalculateTotalWorkflowCost()
    {
        double total = 0;
        foreach (var node in canvasNodes)
        {
            if (string.IsNullOrEmpty(node.Configuration)) continue;
            
            try
            {
                // All AI nodes have Cost property - extract it generically
                var isAiNode = node.NodeType is "OpenAI" or "DeepSeek" or "DeepSeekAgent" or "Anthropic" or "Gemini" or "Mistral" or "Groq" or "PdfOcr" or "LocalLlm" or "LocalLlmAgent";
                if (isAiNode)
                {
                    using var doc = JsonDocument.Parse(node.Configuration);
                    if (doc.RootElement.TryGetProperty("Cost", out var costProp))
                    {
                        total += costProp.GetDouble();
                    }
                }
            }
            catch { }
        }
        return total;
    }

    private record AINodeCostInfo(string IconSvg, string NodeType, string Name, double Cost, long InputTokens, long OutputTokens);
    
    private List<AINodeCostInfo> GetAINodeCostBreakdown()
    {
        var result = new List<AINodeCostInfo>();
        
        foreach (var node in canvasNodes)
        {
            if (string.IsNullOrEmpty(node.Configuration)) continue;
            
            try
            {
                var isAiNode = node.NodeType is "OpenAI" or "DeepSeek" or "DeepSeekAgent" or "Anthropic" or "Gemini" or "Mistral" or "Groq" or "PdfOcr" or "LocalLlm" or "LocalLlmAgent";
                
                if (isAiNode)
                {
                    using var doc = JsonDocument.Parse(node.Configuration);
                    var root = doc.RootElement;
                    
                    double cost = root.TryGetProperty("Cost", out var c) ? c.GetDouble() : 0;
                    long inputTokens = root.TryGetProperty("InputTokens", out var i) ? i.GetInt64() : 0;
                    long outputTokens = root.TryGetProperty("OutputTokens", out var o) ? o.GetInt64() : 0;
                    
                    if (cost > 0 || inputTokens > 0 || outputTokens > 0)
                    {
                        // Get SVG icon from catalog (same as node header rendering)
                        var iconSvg = BuiltInNodeCatalogService.GetIconOverride(node.NodeType) ?? "";
                        result.Add(new AINodeCostInfo(iconSvg, node.NodeType, node.Name, cost, inputTokens, outputTokens));
                    }
                }
            }
            catch { }
        }
        
        return result;
    }
    
    private static string FormatTokenCount(long tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return tokens.ToString();
    }

    #endregion
}
