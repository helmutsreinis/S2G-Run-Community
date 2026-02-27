using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for Storage Table CRUD operations.
/// </summary>
public class StorageTableService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly UsageTrackingService _usageTrackingService;

    public StorageTableService(IDbContextFactory<ApplicationDbContext> dbContextFactory, UsageTrackingService usageTrackingService)
    {
        _dbContextFactory = dbContextFactory;
        _usageTrackingService = usageTrackingService;
    }

    #region Column Schema Operations

    /// <summary>
    /// Get column definitions for a Storage Table node.
    /// </summary>
    public async Task<List<StorageTableColumn>> GetColumnsAsync(Guid storageTableNodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.StorageTableColumns
            .Where(c => c.StorageTableNodeId == storageTableNodeId)
            .OrderBy(c => c.OrderIndex)
            .ToListAsync();
    }

    /// <summary>
    /// Save or update column schema for a Storage Table node.
    /// </summary>
    public async Task SaveColumnsAsync(Guid storageTableNodeId, List<StorageTableColumn> columns)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Remove existing columns
        var existing = await context.StorageTableColumns
            .Where(c => c.StorageTableNodeId == storageTableNodeId)
            .ToListAsync();
        context.StorageTableColumns.RemoveRange(existing);

        // Add new columns
        for (int i = 0; i < columns.Count; i++)
        {
            columns[i].StorageTableNodeId = storageTableNodeId;
            columns[i].OrderIndex = i;
            if (columns[i].Id == Guid.Empty)
                columns[i].Id = Guid.NewGuid();
        }
        context.StorageTableColumns.AddRange(columns);
        await context.SaveChangesAsync();
    }

    #endregion

    #region Record Operations

    /// <summary>
    /// Insert a new record into a Storage Table.
    /// Returns (Success, RecordId, Error) - Error is set if storage limit exceeded.
    /// </summary>
    public async Task<(bool Success, Guid RecordId, string? Error)> InsertRecordAsync(Guid storageTableNodeId, string userId, Dictionary<string, object?> data)
    {
        // Check storage limit before inserting (uses cached check)
        var (canStore, reason) = await _usageTrackingService.CanStoreAsync(userId);
        if (!canStore)
        {
            return (false, Guid.Empty, reason);
        }
        
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var dataJson = JsonSerializer.Serialize(data);
        var record = new StorageTableRecord
        {
            Id = Guid.NewGuid(),
            StorageTableNodeId = storageTableNodeId,
            UserId = userId,
            Timestamp = DateTime.UtcNow,
            DataJson = dataJson
        };

        context.StorageTableRecords.Add(record);
        await context.SaveChangesAsync();
        
        // Track storage usage
        var tableBytes = Encoding.UTF8.GetByteCount(dataJson) + 100; // JSON + overhead
        await _usageTrackingService.UpdateStorageAsync(userId, tableBytes: tableBytes);
        
        // Invalidate cache after storage update
        _usageTrackingService.InvalidateStorageLimitCache(userId);
        
        return (true, record.Id, null);
    }

    /// <summary>
    /// Insert or update a record by ID.
    /// Returns (Success, RecordId, Error) - Error is set if storage limit exceeded for new records.
    /// </summary>
    public async Task<(bool Success, Guid RecordId, string? Error)> UpsertRecordAsync(Guid storageTableNodeId, string userId, Guid? recordId, Dictionary<string, object?> data)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var dataJson = JsonSerializer.Serialize(data);
        StorageTableRecord? record = null;
        if (recordId.HasValue && recordId.Value != Guid.Empty)
        {
            record = await context.StorageTableRecords.FindAsync(recordId.Value);
        }

        bool isNew = record == null;
        
        // Check storage limit before inserting NEW records only (updates don't increase storage)
        if (isNew)
        {
            var (canStore, reason) = await _usageTrackingService.CanStoreAsync(userId);
            if (!canStore)
            {
                return (false, Guid.Empty, reason);
            }
        }
        
        if (record != null)
        {
            // Update existing
            record.DataJson = dataJson;
            record.Timestamp = DateTime.UtcNow;
        }
        else
        {
            // Insert new
            record = new StorageTableRecord
            {
                Id = recordId ?? Guid.NewGuid(),
                StorageTableNodeId = storageTableNodeId,
                UserId = userId,
                Timestamp = DateTime.UtcNow,
                DataJson = dataJson
            };
            context.StorageTableRecords.Add(record);
        }

        await context.SaveChangesAsync();
        
        // Track storage usage for new records only
        if (isNew)
        {
            var tableBytes = Encoding.UTF8.GetByteCount(dataJson) + 100;
            await _usageTrackingService.UpdateStorageAsync(userId, tableBytes: tableBytes);
            
            // Invalidate cache after storage update
            _usageTrackingService.InvalidateStorageLimitCache(userId);
        }
        
        return (true, record.Id, null);
    }

    /// <summary>
    /// Update a record by ID.
    /// Security: Validates that the record belongs to the specified storageTableNodeId.
    /// </summary>
    public async Task<bool> UpdateRecordAsync(Guid recordId, Guid storageTableNodeId, Dictionary<string, object?> data)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Security: Find record only if it belongs to this storage table node
        var record = await context.StorageTableRecords
            .FirstOrDefaultAsync(r => r.Id == recordId && r.StorageTableNodeId == storageTableNodeId);
        if (record == null)
            return false;

        record.DataJson = JsonSerializer.Serialize(data);
        record.Timestamp = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Query records with optional filter.
    /// </summary>
    public async Task<List<StorageTableRecord>> QueryRecordsAsync(
        Guid storageTableNodeId, 
        string? filterColumn = null, 
        string? filterOperator = null, 
        string? filterValue = null,
        int maxResults = 100)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var query = context.StorageTableRecords
            .Where(r => r.StorageTableNodeId == storageTableNodeId)
            .OrderByDescending(r => r.Timestamp)
            .AsQueryable();

        // If filtering by Timestamp column specifically
        if (!string.IsNullOrEmpty(filterColumn) && filterColumn.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.TryParse(filterValue, out var dateValue))
            {
                query = filterOperator switch
                {
                    ">" => query.Where(r => r.Timestamp > dateValue),
                    ">=" => query.Where(r => r.Timestamp >= dateValue),
                    "<" => query.Where(r => r.Timestamp < dateValue),
                    "<=" => query.Where(r => r.Timestamp <= dateValue),
                    "==" or "=" => query.Where(r => r.Timestamp == dateValue),
                    _ => query
                };
            }
        }

        var records = await query.Take(maxResults).ToListAsync();

        // Apply JSON-based filtering if column is not Timestamp
        if (!string.IsNullOrEmpty(filterColumn) && !filterColumn.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
        {
            records = FilterRecordsByColumn(records, filterColumn, filterOperator, filterValue);
        }

        return records;
    }

    /// <summary>
    /// Get record count for a Storage Table.
    /// </summary>
    public async Task<int> GetRecordCountAsync(Guid storageTableNodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.StorageTableRecords
            .Where(r => r.StorageTableNodeId == storageTableNodeId)
            .CountAsync();
    }

    /// <summary>
    /// Delete a single record by ID.
    /// Security: Validates that the record belongs to the specified storageTableNodeId.
    /// </summary>
    public async Task<bool> DeleteRecordAsync(Guid recordId, Guid storageTableNodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Security: Find record only if it belongs to this storage table node
        var record = await context.StorageTableRecords
            .FirstOrDefaultAsync(r => r.Id == recordId && r.StorageTableNodeId == storageTableNodeId);
        if (record == null)
            return false;

        context.StorageTableRecords.Remove(record);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Delete records matching filter criteria.
    /// </summary>
    public async Task<int> DeleteRecordsAsync(
        Guid storageTableNodeId, 
        string? filterColumn = null, 
        string? filterOperator = null, 
        string? filterValue = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Get records to delete using internal query
        var query = context.StorageTableRecords
            .Where(r => r.StorageTableNodeId == storageTableNodeId)
            .AsQueryable();

        // If filtering by Timestamp column specifically
        if (!string.IsNullOrEmpty(filterColumn) && filterColumn.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.TryParse(filterValue, out var dateValue))
            {
                query = filterOperator switch
                {
                    ">" => query.Where(r => r.Timestamp > dateValue),
                    ">=" => query.Where(r => r.Timestamp >= dateValue),
                    "<" => query.Where(r => r.Timestamp < dateValue),
                    "<=" => query.Where(r => r.Timestamp <= dateValue),
                    "==" or "=" => query.Where(r => r.Timestamp == dateValue),
                    _ => query
                };
            }
        }

        var records = await query.ToListAsync();

        // Apply JSON-based filtering if column is not Timestamp
        if (!string.IsNullOrEmpty(filterColumn) && !filterColumn.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
        {
            records = FilterRecordsByColumn(records, filterColumn, filterOperator, filterValue);
        }

        context.StorageTableRecords.RemoveRange(records);
        await context.SaveChangesAsync();
        return records.Count;
    }

    /// <summary>
    /// Apply retention policy - delete records older than specified days.
    /// </summary>
    public async Task<int> ApplyRetentionAsync(Guid storageTableNodeId, int retentionDays)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var oldRecords = await context.StorageTableRecords
            .Where(r => r.StorageTableNodeId == storageTableNodeId && r.Timestamp < cutoff)
            .ToListAsync();

        context.StorageTableRecords.RemoveRange(oldRecords);
        await context.SaveChangesAsync();
        return oldRecords.Count;
    }

    /// <summary>
    /// Get all records for a Storage Table (for data viewer).
    /// </summary>
    public async Task<List<StorageTableRecord>> GetAllRecordsAsync(Guid storageTableNodeId, int maxResults = 1000)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.StorageTableRecords
            .Where(r => r.StorageTableNodeId == storageTableNodeId)
            .OrderByDescending(r => r.Timestamp)
            .Take(maxResults)
            .ToListAsync();
    }
    
    /// <summary>
    /// Clear all storage table records for a user.
    /// </summary>
    public async Task<(int Count, long BytesFreed)> ClearAllForUserAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var records = await context.StorageTableRecords
            .Where(r => r.UserId == userId)
            .ToListAsync();
        
        // Calculate bytes to free
        long bytesFreed = records.Sum(r => Encoding.UTF8.GetByteCount(r.DataJson) + 100);

        context.StorageTableRecords.RemoveRange(records);
        await context.SaveChangesAsync();
        
        // Update storage tracking
        if (records.Any())
        {
            await _usageTrackingService.UpdateStorageAsync(userId, tableBytes: -bytesFreed);
        }
        
        return (records.Count, bytesFreed);
    }

    #endregion

    #region Helper Methods

    private List<StorageTableRecord> FilterRecordsByColumn(
        List<StorageTableRecord> records, 
        string? filterColumn, 
        string? filterOperator, 
        string? filterValue)
    {
        if (string.IsNullOrEmpty(filterColumn) || string.IsNullOrEmpty(filterOperator))
            return records;

        return records.Where(r =>
        {
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.DataJson);
                if (data == null || !data.TryGetValue(filterColumn, out var element))
                    return false;

                var actualValue = GetJsonElementValue(element);
                return MatchesFilter(actualValue, filterOperator, filterValue);
            }
            catch
            {
                return false;
            }
        }).ToList();
    }

    private object? GetJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private bool MatchesFilter(object? actualValue, string filterOperator, string? filterValue)
    {
        var actualStr = actualValue?.ToString() ?? "";
        var filterStr = filterValue ?? "";

        return filterOperator switch
        {
            "==" or "=" => string.Equals(actualStr, filterStr, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(actualStr, filterStr, StringComparison.OrdinalIgnoreCase),
            "Contains" => actualStr.Contains(filterStr, StringComparison.OrdinalIgnoreCase),
            "StartsWith" => actualStr.StartsWith(filterStr, StringComparison.OrdinalIgnoreCase),
            "EndsWith" => actualStr.EndsWith(filterStr, StringComparison.OrdinalIgnoreCase),
            ">" => CompareNumeric(actualValue, filterValue) > 0,
            ">=" => CompareNumeric(actualValue, filterValue) >= 0,
            "<" => CompareNumeric(actualValue, filterValue) < 0,
            "<=" => CompareNumeric(actualValue, filterValue) <= 0,
            _ => false
        };
    }

    private int CompareNumeric(object? actual, string? filter)
    {
        if (double.TryParse(actual?.ToString(), out var actualNum) && 
            double.TryParse(filter, out var filterNum))
        {
            return actualNum.CompareTo(filterNum);
        }
        return string.Compare(actual?.ToString(), filter, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
