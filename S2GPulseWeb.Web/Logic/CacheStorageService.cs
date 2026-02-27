using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Singleton service for managing cache data across workflow executions
/// </summary>
public class CacheStorageService
{
    /// <summary>
    /// Cache storage: WorkflowId -> NodeId -> PropertyName -> CacheEntry
    /// </summary>
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ConcurrentDictionary<string, CacheEntry>>> _storage = new();

    /// <summary>
    /// Set a property value in the cache
    /// </summary>
    public void Set(Guid workflowId, Guid nodeId, string propertyName, object? value, int? expirationMinutes = null)
    {
        var workflowCache = _storage.GetOrAdd(workflowId, _ => new ConcurrentDictionary<Guid, ConcurrentDictionary<string, CacheEntry>>());
        var nodeCache = workflowCache.GetOrAdd(nodeId, _ => new ConcurrentDictionary<string, CacheEntry>());
        
        var entry = new CacheEntry
        {
            Value = value,
            ExpiresAt = expirationMinutes.HasValue && expirationMinutes > 0 
                ? DateTime.UtcNow.AddMinutes(expirationMinutes.Value) 
                : null
        };
        
        nodeCache[propertyName] = entry;
    }

    /// <summary>
    /// Set multiple properties from a dictionary (for SetObject operation)
    /// </summary>
    public void SetObject(Guid workflowId, Guid nodeId, Dictionary<string, object?> properties, int? expirationMinutes = null)
    {
        foreach (var kvp in properties)
        {
            Set(workflowId, nodeId, kvp.Key, kvp.Value, expirationMinutes);
        }
    }

    /// <summary>
    /// Get a property value from the cache
    /// </summary>
    public object? Get(Guid workflowId, Guid nodeId, string propertyName)
    {
        if (!_storage.TryGetValue(workflowId, out var workflowCache)) return null;
        if (!workflowCache.TryGetValue(nodeId, out var nodeCache)) return null;
        if (!nodeCache.TryGetValue(propertyName, out var entry)) return null;
        
        // Check expiration
        if (entry.ExpiresAt.HasValue && entry.ExpiresAt < DateTime.UtcNow)
        {
            nodeCache.TryRemove(propertyName, out _);
            return null;
        }
        
        return entry.Value;
    }

    /// <summary>
    /// Get all cached data for a node
    /// </summary>
    public Dictionary<string, object?> GetAll(Guid workflowId, Guid nodeId)
    {
        if (!_storage.TryGetValue(workflowId, out var workflowCache)) return new();
        if (!workflowCache.TryGetValue(nodeId, out var nodeCache)) return new();
        
        var result = new Dictionary<string, object?>();
        var expiredKeys = new List<string>();
        
        foreach (var kvp in nodeCache)
        {
            if (kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt < DateTime.UtcNow)
            {
                expiredKeys.Add(kvp.Key);
            }
            else
            {
                result[kvp.Key] = kvp.Value.Value;
            }
        }
        
        // Clean up expired entries
        foreach (var key in expiredKeys)
        {
            nodeCache.TryRemove(key, out _);
        }
        
        return result;
    }

    /// <summary>
    /// Get all property keys for a node
    /// </summary>
    public List<string> GetAllKeys(Guid workflowId, Guid nodeId)
    {
        return GetAll(workflowId, nodeId).Keys.ToList();
    }

    /// <summary>
    /// Delete a specific property
    /// </summary>
    public bool Delete(Guid workflowId, Guid nodeId, string propertyName)
    {
        if (!_storage.TryGetValue(workflowId, out var workflowCache)) return false;
        if (!workflowCache.TryGetValue(nodeId, out var nodeCache)) return false;
        
        return nodeCache.TryRemove(propertyName, out _);
    }

    /// <summary>
    /// Clear all cached data for a node
    /// </summary>
    public void Clear(Guid workflowId, Guid nodeId)
    {
        if (!_storage.TryGetValue(workflowId, out var workflowCache)) return;
        workflowCache.TryRemove(nodeId, out _);
    }

    /// <summary>
    /// Clear all cached data for a workflow
    /// </summary>
    public void ClearWorkflow(Guid workflowId)
    {
        _storage.TryRemove(workflowId, out _);
    }

    /// <summary>
    /// Get all cache data for display (workflowId -> nodeId -> properties)
    /// </summary>
    public Dictionary<Guid, Dictionary<Guid, Dictionary<string, CacheDisplayEntry>>> GetAllCacheData()
    {
        var result = new Dictionary<Guid, Dictionary<Guid, Dictionary<string, CacheDisplayEntry>>>();
        
        foreach (var workflowKvp in _storage)
        {
            var workflowData = new Dictionary<Guid, Dictionary<string, CacheDisplayEntry>>();
            
            foreach (var nodeKvp in workflowKvp.Value)
            {
                var nodeData = new Dictionary<string, CacheDisplayEntry>();
                
                foreach (var propKvp in nodeKvp.Value)
                {
                    // Skip expired entries
                    if (propKvp.Value.ExpiresAt.HasValue && propKvp.Value.ExpiresAt < DateTime.UtcNow)
                        continue;
                    
                    nodeData[propKvp.Key] = new CacheDisplayEntry
                    {
                        Value = propKvp.Value.Value,
                        ExpiresAt = propKvp.Value.ExpiresAt
                    };
                }
                
                if (nodeData.Any())
                {
                    workflowData[nodeKvp.Key] = nodeData;
                }
            }
            
            if (workflowData.Any())
            {
                result[workflowKvp.Key] = workflowData;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Cache entry for display purposes
    /// </summary>
    public class CacheDisplayEntry
    {
        public object? Value { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    private class CacheEntry
    {
        public object? Value { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }
}
