using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing custom node definitions and categories.
/// </summary>
public class CustomNodeService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<CustomNodeService> _logger;

    // Cache for quick lookup during workflow execution
    private static readonly Dictionary<string, CustomNodeDefinition> _definitionCache = new();
    private static List<CustomNodeDefinition>? _definitionsListCache = null;
    private static List<CustomNodeCategory>? _categoriesListCache = null;
    private static readonly Dictionary<Guid, List<CustomNodeDefinition>> _categoryNodesCache = new();
    private static bool _allNodesLoaded = false; // Track if all nodes loaded for search
    private static readonly object _cacheLock = new();
    private static DateTime _lastCacheRefresh = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(20);
    
    // Lightweight catalog cache (refreshed hourly) - for Designer palette display
    private static List<CustomNodeCatalogItem>? _catalogCache = null;
    private static DateTime _catalogCacheRefresh = DateTime.MinValue;
    private static readonly TimeSpan CatalogCacheExpiration = TimeSpan.FromHours(1);

    public CustomNodeService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<CustomNodeService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    #region Categories

    public async Task<List<CustomNodeCategory>> GetCategoriesAsync()
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_categoriesListCache != null && DateTime.UtcNow - _lastCacheRefresh < CacheExpiration)
            {
                return _categoriesListCache;
            }
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var categories = await db.CustomNodeCategories
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();

        lock (_cacheLock)
        {
            _categoriesListCache = categories;
            _lastCacheRefresh = DateTime.UtcNow;
        }

        return categories;
    }

    public async Task<CustomNodeCategory?> GetCategoryByIdAsync(Guid id)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.CustomNodeCategories.FindAsync(id);
    }

    public async Task<CustomNodeCategory> CreateCategoryAsync(CustomNodeCategory category)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        category.Id = Guid.NewGuid();
        category.CreatedAt = DateTime.UtcNow;
        
        db.CustomNodeCategories.Add(category);
        await db.SaveChangesAsync();
        
        InvalidateCache();
        _logger.LogInformation("Created custom node category: {Name}", category.Name);
        return category;
    }

    public async Task<CustomNodeCategory> UpdateCategoryAsync(CustomNodeCategory category)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var existing = await db.CustomNodeCategories.FindAsync(category.Id);
        if (existing == null)
            throw new InvalidOperationException($"Category {category.Id} not found");

        existing.Name = category.Name;
        existing.Description = category.Description;
        existing.IconEmoji = category.IconEmoji;
        existing.IconSvg = category.IconSvg;
        existing.DisplayOrder = category.DisplayOrder;
        existing.IsEnabled = category.IsEnabled;

        await db.SaveChangesAsync();
        
        InvalidateCache();
        _logger.LogInformation("Updated custom node category: {Name}", category.Name);
        return existing;
    }

    public async Task DeleteCategoryAsync(Guid id)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var category = await db.CustomNodeCategories.FindAsync(id);
        if (category != null)
        {
            db.CustomNodeCategories.Remove(category);
            await db.SaveChangesAsync();
            InvalidateCache();
            _logger.LogInformation("Deleted custom node category: {Name}", category.Name);
        }
    }

    /// <summary>
    /// Gets enabled custom node definitions for a specific category.
    /// Uses global cache for persistence across navigation.
    /// </summary>
    public async Task<List<CustomNodeDefinition>> GetNodesForCategoryAsync(Guid categoryId)
    {
        // Check global cache first
        lock (_cacheLock)
        {
            if (_categoryNodesCache.TryGetValue(categoryId, out var cached))
                return cached;
        }
        
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var nodes = await db.CustomNodeDefinitions
            .Include(d => d.Category)
            .Include(d => d.InputFields.OrderBy(f => f.DisplayOrder))
            .Include(d => d.OutputParameters.OrderBy(p => p.DisplayOrder))
            .Include(d => d.ConnectionTags.OrderBy(t => t.DisplayOrder))
            .Where(d => d.CategoryId == categoryId && d.IsEnabled)
            .OrderBy(d => d.DisplayName)
            .ToListAsync();
        
        // Store in global cache
        lock (_cacheLock)
        {
            _categoryNodesCache[categoryId] = nodes;
        }
        
        return nodes;
    }
    
    /// <summary>
    /// Gets cached nodes for a category (synchronous, returns empty if not loaded).
    /// </summary>
    public List<CustomNodeDefinition> GetCachedNodesForCategory(Guid categoryId)
    {
        lock (_cacheLock)
        {
            return _categoryNodesCache.TryGetValue(categoryId, out var cached) 
                ? cached 
                : new List<CustomNodeDefinition>();
        }
    }
    
    /// <summary>
    /// Ensures all nodes for all categories are loaded into cache (for search).
    /// </summary>
    public async Task EnsureAllNodesLoadedAsync()
    {
        // Check if all nodes already loaded
        lock (_cacheLock)
        {
            if (_allNodesLoaded)
                return;
        }
        
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var allNodes = await db.CustomNodeDefinitions
            .Include(d => d.Category)
            .Include(d => d.InputFields.OrderBy(f => f.DisplayOrder))
            .Include(d => d.OutputParameters.OrderBy(p => p.DisplayOrder))
            .Include(d => d.ConnectionTags.OrderBy(t => t.DisplayOrder))
            .Where(d => d.IsEnabled)
            .OrderBy(d => d.DisplayName)
            .ToListAsync();
        
        // Group by category and store in cache
        lock (_cacheLock)
        {
            foreach (var group in allNodes.GroupBy(n => n.CategoryId ?? Guid.Empty))
            {
                if (group.Key != Guid.Empty)
                    _categoryNodesCache[group.Key] = group.ToList();
            }
            _allNodesLoaded = true;
        }
    }

    #endregion

    #region Lightweight Catalog
    
    /// <summary>
    /// Gets lightweight catalog items for Designer palette display.
    /// Only loads minimal data needed for display - no related entities.
    /// Uses 1-hour cache for performance.
    /// </summary>
    public async Task<List<CustomNodeCatalogItem>> GetCatalogItemsAsync()
    {
        // Check catalog cache first
        lock (_cacheLock)
        {
            if (_catalogCache != null && DateTime.UtcNow - _catalogCacheRefresh < CatalogCacheExpiration)
                return _catalogCache;
        }
        
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var items = await db.CustomNodeDefinitions
            .Where(d => d.IsEnabled)
            .Select(d => new CustomNodeCatalogItem
            {
                Id = d.Id,
                NodeTypeKey = d.NodeTypeKey,
                DisplayName = d.DisplayName,
                IconSvg = d.IconSvg,
                IconFallbackEmoji = d.IconFallbackEmoji,
                CategoryId = d.CategoryId
            })
            .OrderBy(d => d.DisplayName)
            .ToListAsync();
        
        lock (_cacheLock)
        {
            _catalogCache = items;
            _catalogCacheRefresh = DateTime.UtcNow;
        }
        
        _logger.LogDebug("Loaded {Count} catalog items (lightweight)", items.Count);
        return items;
    }
    
    /// <summary>
    /// Gets lightweight catalog items for a specific category.
    /// </summary>
    public async Task<List<CustomNodeCatalogItem>> GetCatalogItemsForCategoryAsync(Guid categoryId)
    {
        var allItems = await GetCatalogItemsAsync();
        return allItems.Where(i => i.CategoryId == categoryId).ToList();
    }
    
    /// <summary>
    /// Gets lightweight list items for the Admin Node Designer.
    /// Only loads minimal data needed for the list display - full definition loaded on edit.
    /// Includes disabled nodes (for admin visibility).
    /// </summary>
    public async Task<List<AdminNodeListItem>> GetAdminListItemsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var items = await db.CustomNodeDefinitions
            .Select(d => new AdminNodeListItem
            {
                Id = d.Id,
                NodeTypeKey = d.NodeTypeKey,
                DisplayName = d.DisplayName,
                IconSvg = d.IconSvg,
                IsEnabled = d.IsEnabled,
                ExecutionType = d.ExecutionType,
                Version = d.Version,
                CategoryId = d.CategoryId,
                CategoryName = d.Category != null ? d.Category.Name : null,
                CategoryEmoji = d.Category != null ? d.Category.IconEmoji : null
            })
            .OrderBy(d => d.CategoryName ?? "ZZZZ") // Uncategorized last
            .ThenBy(d => d.DisplayName)
            .ToListAsync();
        
        _logger.LogDebug("Loaded {Count} admin list items (lightweight)", items.Count);
        return items;
    }
    
    /// <summary>
    /// Batch loads full definitions by IDs. Used when opening a workflow
    /// to load only the custom nodes actually used in that workflow.
    /// </summary>
    public async Task<List<CustomNodeDefinition>> GetDefinitionsByIdsAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        if (!idList.Any()) return new List<CustomNodeDefinition>();
        
        // Check cache for already-loaded definitions
        var result = new List<CustomNodeDefinition>();
        var missingIds = new List<Guid>();
        
        lock (_cacheLock)
        {
            foreach (var id in idList)
            {
                var cached = _definitionCache.Values.FirstOrDefault(d => d.Id == id);
                if (cached != null)
                    result.Add(cached);
                else
                    missingIds.Add(id);
            }
        }
        
        if (missingIds.Any())
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var loaded = await db.CustomNodeDefinitions
                .Include(d => d.InputFields.OrderBy(f => f.DisplayOrder))
                .Include(d => d.OutputParameters.OrderBy(p => p.DisplayOrder))
                .Include(d => d.ConnectionTags.OrderBy(t => t.DisplayOrder))
                .Include(d => d.LogConfigs)
                .Where(d => missingIds.Contains(d.Id) && d.IsEnabled)
                .ToListAsync();
            
            lock (_cacheLock)
            {
                foreach (var def in loaded)
                {
                    _definitionCache[def.NodeTypeKey] = def;
                    result.Add(def);
                }
            }
            
            _logger.LogDebug("Loaded {Count} definitions by IDs (batch)", loaded.Count);
        }
        
        return result;
    }
    
    /// <summary>
    /// Batch loads full definitions by NodeTypeKeys. Used for pre-loading
    /// custom nodes when opening a workflow.
    /// Handles both prefixed (Custom_Foo) and raw (Foo) key formats,
    /// and performs case-insensitive matching.
    /// </summary>
    public async Task<List<CustomNodeDefinition>> GetDefinitionsByKeysAsync(IEnumerable<string> nodeTypeKeys)
    {
        var keyList = nodeTypeKeys.ToList();
        if (!keyList.Any()) return new List<CustomNodeDefinition>();
        
        // Check cache for already-loaded definitions (try both raw key and canonical form)
        var result = new List<CustomNodeDefinition>();
        var missingKeys = new List<string>();
        
        lock (_cacheLock)
        {
            foreach (var key in keyList)
            {
                // Try exact match first, then canonical "Custom_" prefixed form
                if (_definitionCache.TryGetValue(key, out var cached) ||
                    _definitionCache.TryGetValue(EnsureCustomPrefix(key), out cached))
                    result.Add(cached);
                else
                    missingKeys.Add(key);
            }
        }
        
        if (missingKeys.Any())
        {
            // Build both forms for each key so the DB query matches regardless of prefix
            var lookupKeys = missingKeys
                .SelectMany(k => new[] { k.ToLower(), EnsureCustomPrefix(k).ToLower(), StripCustomPrefix(k).ToLower() })
                .Distinct()
                .ToList();
            
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var loaded = await db.CustomNodeDefinitions
                .Include(d => d.InputFields.OrderBy(f => f.DisplayOrder))
                .Include(d => d.OutputParameters.OrderBy(p => p.DisplayOrder))
                .Include(d => d.ConnectionTags.OrderBy(t => t.DisplayOrder))
                .Include(d => d.LogConfigs)
                .Where(d => lookupKeys.Contains(d.NodeTypeKey.ToLower()) && d.IsEnabled)
                .ToListAsync();
            
            lock (_cacheLock)
            {
                foreach (var def in loaded)
                {
                    _definitionCache[def.NodeTypeKey] = def;
                    result.Add(def);
                }
            }
            
            _logger.LogInformation("GetDefinitionsByKeysAsync: requested={Requested}, lookupKeys={LookupKeys}, loaded={Loaded}",
                string.Join(", ", missingKeys), string.Join(", ", lookupKeys), loaded.Count);
        }
        
        return result;
    }
    
    #endregion

    #region Definitions

    public async Task<List<CustomNodeDefinition>> GetDefinitionsAsync(bool includeDisabled = false)
    {
        // For enabled-only queries, use cache if available
        if (!includeDisabled)
        {
            lock (_cacheLock)
            {
                if (_definitionsListCache != null && DateTime.UtcNow - _lastCacheRefresh < CacheExpiration)
                {
                    return _definitionsListCache;
                }
            }
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var query = db.CustomNodeDefinitions
            .Include(d => d.Category)
            .Include(d => d.InputFields.OrderBy(f => f.DisplayOrder))
            .Include(d => d.OutputParameters.OrderBy(p => p.DisplayOrder))
            .Include(d => d.ConnectionTags.OrderBy(t => t.DisplayOrder))
            .Include(d => d.LogConfigs)
            .AsQueryable();

        if (!includeDisabled)
            query = query.Where(d => d.IsEnabled);

        var definitions = await query
            .OrderBy(d => d.Category != null ? d.Category.DisplayOrder : 999)
            .ThenBy(d => d.DisplayName)
            .ToListAsync();
        
        // Populate cache for fast sync lookups in NodeExecutorFactory
        lock (_cacheLock)
        {
            foreach (var def in definitions.Where(d => d.IsEnabled))
            {
                _definitionCache[def.NodeTypeKey] = def;
            }
            
            // Cache the list for enabled-only queries
            if (!includeDisabled)
            {
                _definitionsListCache = definitions;
            }
            
            _lastCacheRefresh = DateTime.UtcNow;
        }
        
        return definitions;
    }

    public async Task<CustomNodeDefinition?> GetDefinitionByIdAsync(Guid id)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        return await db.CustomNodeDefinitions
            .Include(d => d.Category)
            .Include(d => d.InputFields.OrderBy(f => f.DisplayOrder))
            .Include(d => d.OutputParameters.OrderBy(p => p.DisplayOrder))
            .Include(d => d.ConnectionTags.OrderBy(t => t.DisplayOrder))
            .Include(d => d.LogConfigs)
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<CustomNodeDefinition?> GetDefinitionByKeyAsync(string nodeTypeKey)
    {
        var canonicalKey = EnsureCustomPrefix(nodeTypeKey);
        
        // Check cache first (try both raw and canonical forms)
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _lastCacheRefresh < CacheExpiration)
            {
                if (_definitionCache.TryGetValue(nodeTypeKey, out var cached) ||
                    _definitionCache.TryGetValue(canonicalKey, out cached))
                {
                    return cached;
                }
            }
        }

        // Query DB with case-insensitive match on both forms
        var keyLower = nodeTypeKey.ToLower();
        var canonicalLower = canonicalKey.ToLower();
        
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var definition = await db.CustomNodeDefinitions
            .Include(d => d.Category)
            .Include(d => d.InputFields.OrderBy(f => f.DisplayOrder))
            .Include(d => d.OutputParameters.OrderBy(p => p.DisplayOrder))
            .Include(d => d.ConnectionTags.OrderBy(t => t.DisplayOrder))
            .Include(d => d.LogConfigs)
            .FirstOrDefaultAsync(d => (d.NodeTypeKey.ToLower() == keyLower || d.NodeTypeKey.ToLower() == canonicalLower) && d.IsEnabled);

        if (definition != null)
        {
            lock (_cacheLock)
            {
                _definitionCache[definition.NodeTypeKey] = definition;
                _lastCacheRefresh = DateTime.UtcNow;
            }
        }

        return definition;
    }

    /// <summary>
    /// Synchronous cache lookup for use in NodeExecutorFactory.
    /// Returns null if not cached - caller should handle async fallback.
    /// </summary>
    public CustomNodeDefinition? GetDefinitionByKeySync(string nodeTypeKey)
    {
        var canonicalKey = EnsureCustomPrefix(nodeTypeKey);
        
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _lastCacheRefresh < CacheExpiration)
            {
                if (_definitionCache.TryGetValue(nodeTypeKey, out var cached) ||
                    _definitionCache.TryGetValue(canonicalKey, out cached))
                {
                    return cached;
                }
            }
        }
        return null;
    }

    public async Task<CustomNodeDefinition> CreateDefinitionAsync(CustomNodeDefinition definition)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        // Validate NodeTypeKey format - ensure exactly one Custom_ prefix
        var rawKey = StripCustomPrefix(definition.NodeTypeKey);
        definition.NodeTypeKey = $"Custom_{rawKey}";

        // Check for unique key (case-insensitive)
        var keyLower = definition.NodeTypeKey.ToLower();
        var exists = await db.CustomNodeDefinitions
            .AnyAsync(d => d.NodeTypeKey == definition.NodeTypeKey);
        if (exists)
            throw new InvalidOperationException($"Node type key '{definition.NodeTypeKey}' already exists");

        definition.Id = Guid.NewGuid();
        definition.CreatedAt = DateTime.UtcNow;
        definition.UpdatedAt = DateTime.UtcNow;
        definition.Version = 1;

        // Assign IDs to child entities
        foreach (var field in definition.InputFields)
        {
            field.Id = Guid.NewGuid();
            field.NodeDefinitionId = definition.Id;
        }
        foreach (var param in definition.OutputParameters)
        {
            param.Id = Guid.NewGuid();
            param.NodeDefinitionId = definition.Id;
        }
        foreach (var tag in definition.ConnectionTags)
        {
            tag.Id = Guid.NewGuid();
            tag.NodeDefinitionId = definition.Id;
        }
        foreach (var logConfig in definition.LogConfigs)
        {
            logConfig.Id = Guid.NewGuid();
            logConfig.NodeDefinitionId = definition.Id;
        }

        db.CustomNodeDefinitions.Add(definition);
        await db.SaveChangesAsync();

        InvalidateCache();
        _logger.LogInformation("Created custom node definition: {NodeTypeKey}", definition.NodeTypeKey);
        
        return definition;
    }

    public async Task<CustomNodeDefinition> UpdateDefinitionAsync(CustomNodeDefinition definition)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        // First, update scalar properties
        var existing = await db.CustomNodeDefinitions
            .FirstOrDefaultAsync(d => d.Id == definition.Id);

        if (existing == null)
            throw new InvalidOperationException($"Definition {definition.Id} not found");

        // Update scalar properties
        existing.DisplayName = definition.DisplayName;
        existing.Description = definition.Description;
        existing.IconSvg = definition.IconSvg;
        existing.IconFallbackEmoji = definition.IconFallbackEmoji;
        existing.CategoryId = definition.CategoryId;
        existing.ExecutionType = definition.ExecutionType;
        existing.ExecutionDelayMs = definition.ExecutionDelayMs;
        existing.TimeoutSeconds = definition.TimeoutSeconds;
        existing.Script = definition.Script;
        existing.InitializationScript = definition.InitializationScript;
        existing.InputSchemaJson = definition.InputSchemaJson;
        existing.DefaultConfigurationJson = definition.DefaultConfigurationJson;
        existing.IsEnabled = definition.IsEnabled;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version++;

        // Delete existing child entities using direct ExecuteDeleteAsync (bypasses tracking)
        await db.CustomNodeInputFields.Where(f => f.NodeDefinitionId == existing.Id).ExecuteDeleteAsync();
        await db.CustomNodeOutputParameters.Where(p => p.NodeDefinitionId == existing.Id).ExecuteDeleteAsync();
        await db.CustomNodeConnectionTags.Where(t => t.NodeDefinitionId == existing.Id).ExecuteDeleteAsync();
        await db.CustomNodeLogConfigs.Where(l => l.NodeDefinitionId == existing.Id).ExecuteDeleteAsync();

        // Add new child entities directly to DbSets
        foreach (var field in definition.InputFields)
        {
            db.CustomNodeInputFields.Add(new CustomNodeInputField
            {
                Id = Guid.NewGuid(),
                NodeDefinitionId = existing.Id,
                FieldName = field.FieldName,
                DisplayLabel = field.DisplayLabel,
                PlaceholderText = field.PlaceholderText,
                HelpText = field.HelpText,
                FieldType = field.FieldType,
                DefaultValue = field.DefaultValue,
                IsRequired = field.IsRequired,
                AllowPlaceholders = field.AllowPlaceholders,
                ValidationRegex = field.ValidationRegex,
                ExpectedDataType = field.ExpectedDataType,
                JsonSchemaValidation = field.JsonSchemaValidation,
                SelectOptionsJson = field.SelectOptionsJson,
                DisplayOrder = field.DisplayOrder
            });
        }
        foreach (var param in definition.OutputParameters)
        {
            db.CustomNodeOutputParameters.Add(new CustomNodeOutputParameter
            {
                Id = Guid.NewGuid(),
                NodeDefinitionId = existing.Id,
                ParameterName = param.ParameterName,
                Description = param.Description,
                DataType = param.DataType,
                DisplayOrder = param.DisplayOrder
            });
        }
        foreach (var tag in definition.ConnectionTags)
        {
            db.CustomNodeConnectionTags.Add(new CustomNodeConnectionTag
            {
                Id = Guid.NewGuid(),
                NodeDefinitionId = existing.Id,
                TagName = tag.TagName,
                Description = tag.Description,
                Color = tag.Color,
                ConditionDescription = tag.ConditionDescription ?? "",
                DisplayOrder = tag.DisplayOrder
            });
        }
        foreach (var logConfig in definition.LogConfigs)
        {
            db.CustomNodeLogConfigs.Add(new CustomNodeLogConfig
            {
                Id = Guid.NewGuid(),
                NodeDefinitionId = existing.Id,
                LogTarget = logConfig.LogTarget,
                TargetName = logConfig.TargetName,
                LogLevel = logConfig.LogLevel,
                IsEnabled = logConfig.IsEnabled,
                MessageFormat = logConfig.MessageFormat
            });
        }

        await db.SaveChangesAsync();

        InvalidateCache();
        _logger.LogInformation("Updated custom node definition: {NodeTypeKey} (v{Version})", 
            existing.NodeTypeKey, existing.Version);

        return existing;
    }

    public async Task DeleteDefinitionAsync(Guid id)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        
        var definition = await db.CustomNodeDefinitions.FindAsync(id);
        if (definition != null)
        {
            db.CustomNodeDefinitions.Remove(definition);
            await db.SaveChangesAsync();
            
            InvalidateCache();
            _logger.LogInformation("Deleted custom node definition: {NodeTypeKey}", definition.NodeTypeKey);
        }
    }

    #endregion

    #region Import/Export

    public async Task<string> ExportAsync(Guid definitionId)
    {
        var definition = await GetDefinitionByIdAsync(definitionId);
        if (definition == null)
            throw new InvalidOperationException($"Definition {definitionId} not found");

        var exportData = new CustomNodeExportModel
        {
            ExportVersion = 1,
            ExportedAt = DateTime.UtcNow,
            Definition = new CustomNodeDefinitionExport
            {
                NodeTypeKey = definition.NodeTypeKey,
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                IconSvg = definition.IconSvg,
                IconFallbackEmoji = definition.IconFallbackEmoji,
                CategoryName = definition.Category?.Name,
                ExecutionType = definition.ExecutionType.ToString(),
                ExecutionDelayMs = definition.ExecutionDelayMs,
                TimeoutSeconds = definition.TimeoutSeconds,
                Script = definition.Script,
                InitializationScript = definition.InitializationScript,
                InputSchemaJson = definition.InputSchemaJson,
                DefaultConfigurationJson = definition.DefaultConfigurationJson,
                InputFields = definition.InputFields.Select(f => new CustomNodeInputFieldExport
                {
                    FieldName = f.FieldName,
                    DisplayLabel = f.DisplayLabel,
                    PlaceholderText = f.PlaceholderText,
                    HelpText = f.HelpText,
                    FieldType = f.FieldType.ToString(),
                    DefaultValue = f.DefaultValue,
                    IsRequired = f.IsRequired,
                    AllowPlaceholders = f.AllowPlaceholders,
                    ValidationRegex = f.ValidationRegex,
                    ExpectedDataType = f.ExpectedDataType,
                    JsonSchemaValidation = f.JsonSchemaValidation,
                    SelectOptionsJson = f.SelectOptionsJson,
                    DisplayOrder = f.DisplayOrder
                }).ToList(),
                OutputParameters = definition.OutputParameters.Select(p => new CustomNodeOutputParameterExport
                {
                    ParameterName = p.ParameterName,
                    Description = p.Description,
                    DataType = p.DataType,
                    DisplayOrder = p.DisplayOrder
                }).ToList(),
                ConnectionTags = definition.ConnectionTags.Select(t => new CustomNodeConnectionTagExport
                {
                    TagName = t.TagName,
                    Description = t.Description,
                    Color = t.Color,
                    ConditionDescription = t.ConditionDescription,
                    DisplayOrder = t.DisplayOrder
                }).ToList(),
                LogConfigs = definition.LogConfigs.Select(l => new CustomNodeLogConfigExport
                {
                    LogTarget = l.LogTarget.ToString(),
                    TargetName = l.TargetName,
                    LogLevel = l.LogLevel.ToString(),
                    IsEnabled = l.IsEnabled,
                    MessageFormat = l.MessageFormat
                }).ToList()
            }
        };

        return JsonSerializer.Serialize(exportData, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public async Task<CustomNodeDefinition> ImportAsync(string json)
    {
        var exportData = JsonSerializer.Deserialize<CustomNodeExportModel>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (exportData?.Definition == null)
            throw new InvalidOperationException("Invalid export format");

        var def = exportData.Definition;

        // Parse enums
        if (!Enum.TryParse<CustomNodeExecutionType>(def.ExecutionType, out var execType))
            execType = CustomNodeExecutionType.DataTransformation;

        var definition = new CustomNodeDefinition
        {
            NodeTypeKey = def.NodeTypeKey,
            DisplayName = def.DisplayName,
            Description = def.Description,
            IconSvg = def.IconSvg ?? "",
            IconFallbackEmoji = def.IconFallbackEmoji,
            ExecutionType = execType,
            ExecutionDelayMs = def.ExecutionDelayMs,
            TimeoutSeconds = def.TimeoutSeconds,
            Script = def.Script ?? "",
            InitializationScript = def.InitializationScript,
            InputSchemaJson = def.InputSchemaJson,
            DefaultConfigurationJson = def.DefaultConfigurationJson,
            IsEnabled = true
        };

        // Resolve category by name if provided
        if (!string.IsNullOrEmpty(def.CategoryName))
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var category = await db.CustomNodeCategories
                .FirstOrDefaultAsync(c => c.Name == def.CategoryName);
            if (category != null)
                definition.CategoryId = category.Id;
        }

        // Map input fields
        foreach (var f in def.InputFields ?? new List<CustomNodeInputFieldExport>())
        {
            if (!Enum.TryParse<CustomFieldType>(f.FieldType, out var fieldType))
                fieldType = CustomFieldType.Text;

            definition.InputFields.Add(new CustomNodeInputField
            {
                FieldName = f.FieldName,
                DisplayLabel = f.DisplayLabel,
                PlaceholderText = f.PlaceholderText,
                HelpText = f.HelpText,
                FieldType = fieldType,
                DefaultValue = f.DefaultValue,
                IsRequired = f.IsRequired,
                AllowPlaceholders = f.AllowPlaceholders,
                ValidationRegex = f.ValidationRegex,
                ExpectedDataType = f.ExpectedDataType,
                JsonSchemaValidation = f.JsonSchemaValidation,
                SelectOptionsJson = f.SelectOptionsJson,
                DisplayOrder = f.DisplayOrder
            });
        }

        // Map output parameters
        foreach (var p in def.OutputParameters ?? new List<CustomNodeOutputParameterExport>())
        {
            definition.OutputParameters.Add(new CustomNodeOutputParameter
            {
                ParameterName = p.ParameterName,
                Description = p.Description,
                DataType = p.DataType ?? "string",
                DisplayOrder = p.DisplayOrder
            });
        }

        // Map connection tags
        foreach (var t in def.ConnectionTags ?? new List<CustomNodeConnectionTagExport>())
        {
            definition.ConnectionTags.Add(new CustomNodeConnectionTag
            {
                TagName = t.TagName,
                Description = t.Description,
                Color = t.Color,
                ConditionDescription = t.ConditionDescription ?? "",
                DisplayOrder = t.DisplayOrder
            });
        }

        // Map log configs
        foreach (var l in def.LogConfigs ?? new List<CustomNodeLogConfigExport>())
        {
            if (!Enum.TryParse<CustomLogTarget>(l.LogTarget, out var logTarget))
                logTarget = CustomLogTarget.Variable;
            if (!Enum.TryParse<NodeLogLevel>(l.LogLevel, out var logLevel))
                logLevel = NodeLogLevel.Info;

            definition.LogConfigs.Add(new CustomNodeLogConfig
            {
                LogTarget = logTarget,
                TargetName = l.TargetName,
                LogLevel = logLevel,
                IsEnabled = l.IsEnabled,
                MessageFormat = l.MessageFormat
            });
        }

        return await CreateDefinitionAsync(definition);
    }

    #endregion

    #region Cache Management

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _definitionCache.Clear();
            _definitionsListCache = null;
            _categoriesListCache = null;
            _categoryNodesCache.Clear();
            _allNodesLoaded = false;
            _catalogCache = null;
            _catalogCacheRefresh = DateTime.MinValue;
            _lastCacheRefresh = DateTime.MinValue;
        }
    }

    public async Task RefreshCacheAsync()
    {
        var definitions = await GetDefinitionsAsync(includeDisabled: false);
        
        lock (_cacheLock)
        {
            _definitionCache.Clear();
            foreach (var def in definitions)
            {
                _definitionCache[def.NodeTypeKey] = def;
            }
            _lastCacheRefresh = DateTime.UtcNow;
        }
        
        _logger.LogInformation("Refreshed custom node cache with {Count} definitions", definitions.Count);
    }

    /// <summary>Strips the "Custom_" prefix from a key if present.</summary>
    private static string StripCustomPrefix(string key) =>
        key.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase) ? key[7..] : key;

    /// <summary>Ensures the key has the "Custom_" prefix.</summary>
    private static string EnsureCustomPrefix(string key) =>
        key.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase) ? key : $"Custom_{key}";

    #endregion

    #region Seeding

    /// <summary>
    /// Seeds default node categories and imports custom node definitions from the custom-nodes/ directory.
    /// Idempotent — skips if categories already exist.
    /// </summary>
    public async Task SeedCategoriesAndNodesAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        if (await db.CustomNodeCategories.AnyAsync())
        {
            _logger.LogInformation("Node categories already exist, skipping seed");
            return;
        }

        _logger.LogInformation("Seeding node categories and custom node definitions...");

        // --- 1. Seed categories with stable GUIDs matching builtin-node-catalog.json ---
        var categories = new List<CustomNodeCategory>
        {
            // Built-in node catalog categories (GUIDs match builtin-node-catalog.json)
            new() { Id = Guid.Parse("6e37a031-abca-4f18-8c19-1c8bf718f7ce"), Name = "Microsoft 365",       IconEmoji = "📊", DisplayOrder = 0 },
            new() { Id = Guid.Parse("cb0cd617-c7bf-4a3b-98e4-dc4cb15a750d"), Name = "Platform Tools",      IconEmoji = "🔧", DisplayOrder = 1 },
            new() { Id = Guid.Parse("39a7acaf-c68c-4f55-abb9-5cdfad1ee92e"), Name = "Web & HTTP",          IconEmoji = "🌐", DisplayOrder = 2 },
            new() { Id = Guid.Parse("a303d9c4-cc4b-4a51-a8ca-75befec9a979"), Name = "AI & Machine Learning", IconEmoji = "🤖", DisplayOrder = 3 },
            new() { Id = Guid.Parse("b853b573-e9cd-4b5f-8eba-3a32035523fb"), Name = "Azure Services",      IconEmoji = "☁️", DisplayOrder = 4 },

            // Custom node categories (referenced by categoryName in custom-nodes/*.json)
            new() { Id = Guid.NewGuid(), Name = "Data Transformation",         IconEmoji = "🔄", DisplayOrder = 5 },
            new() { Id = Guid.NewGuid(), Name = "Microsoft Partner Center",     IconEmoji = "🤝", DisplayOrder = 6 },
            new() { Id = Guid.NewGuid(), Name = "Microsoft Graph",             IconEmoji = "📈", DisplayOrder = 7 },
            new() { Id = Guid.NewGuid(), Name = "Integration",                 IconEmoji = "🔗", DisplayOrder = 8 },
            new() { Id = Guid.NewGuid(), Name = "Partner Center Integrations",  IconEmoji = "🏢", DisplayOrder = 9 },
        };

        db.CustomNodeCategories.AddRange(categories);
        await db.SaveChangesAsync();
        _logger.LogInformation("Seeded {Count} node categories", categories.Count);

        // --- 2. Import custom node definitions from custom-nodes/ directory ---
        var customNodesDir = Path.Combine(AppContext.BaseDirectory, "custom-nodes");
        if (!Directory.Exists(customNodesDir))
        {
            _logger.LogWarning("custom-nodes directory not found at {Path}, skipping node import", customNodesDir);
            return;
        }

        var jsonFiles = Directory.GetFiles(customNodesDir, "*.json");
        var imported = 0;
        var skipped = 0;

        foreach (var file in jsonFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                await ImportAsync(json);
                imported++;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                skipped++;
                _logger.LogDebug("Skipped duplicate node from {File}: {Message}", Path.GetFileName(file), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import node from {File}", Path.GetFileName(file));
            }
        }

        _logger.LogInformation("Custom node seeding complete: {Imported} imported, {Skipped} skipped from {Total} files",
            imported, skipped, jsonFiles.Length);

        InvalidateCache();
    }

    #endregion
}

#region Export Models

public class CustomNodeExportModel
{
    public int ExportVersion { get; set; }
    public DateTime ExportedAt { get; set; }
    public CustomNodeDefinitionExport Definition { get; set; } = new();
}

public class CustomNodeDefinitionExport
{
    public string NodeTypeKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string? IconSvg { get; set; }
    public string? IconFallbackEmoji { get; set; }
    public string? CategoryName { get; set; }
    public string ExecutionType { get; set; } = "DataTransformation";
    public int ExecutionDelayMs { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string? Script { get; set; }
    public string? InitializationScript { get; set; }
    public string? InputSchemaJson { get; set; }
    public string? DefaultConfigurationJson { get; set; }
    public List<CustomNodeInputFieldExport> InputFields { get; set; } = new();
    public List<CustomNodeOutputParameterExport> OutputParameters { get; set; } = new();
    public List<CustomNodeConnectionTagExport> ConnectionTags { get; set; } = new();
    public List<CustomNodeLogConfigExport> LogConfigs { get; set; } = new();
}

public class CustomNodeInputFieldExport
{
    public string FieldName { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public string? PlaceholderText { get; set; }
    public string? HelpText { get; set; }
    public string FieldType { get; set; } = "Text";
    public string? DefaultValue { get; set; }
    public bool IsRequired { get; set; }
    public bool AllowPlaceholders { get; set; } = true;
    public string? ValidationRegex { get; set; }
    public string? ExpectedDataType { get; set; }
    public string? JsonSchemaValidation { get; set; }
    public string? SelectOptionsJson { get; set; }
    public int DisplayOrder { get; set; }
}

public class CustomNodeOutputParameterExport
{
    public string ParameterName { get; set; } = "";
    public string? Description { get; set; }
    public string? DataType { get; set; }
    public int DisplayOrder { get; set; }
}

public class CustomNodeConnectionTagExport
{
    public string TagName { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public string? ConditionDescription { get; set; }
    public int DisplayOrder { get; set; }
}

public class CustomNodeLogConfigExport
{
    public string LogTarget { get; set; } = "Variable";
    public string TargetName { get; set; } = "";
    public string LogLevel { get; set; } = "Info";
    public bool IsEnabled { get; set; } = true;
    public string? MessageFormat { get; set; }
}

#endregion
