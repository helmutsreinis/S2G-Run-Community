using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Storage Client node for performing CRUD operations against a connected Storage Table.
/// Supports: Query, Insert, Upsert, Update, Delete, DeleteByFilter operations.
/// </summary>
public class StorageClientNode : BaseNodeExecutor
{
    private readonly StorageTableService _storageService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private Guid? _connectedStorageTableNodeId;
    private string? _userId;

    public StorageClientNode(NodeExecutionManager executionManager, StorageTableService storageService, IDbContextFactory<ApplicationDbContext> dbContextFactory) 
        : base(executionManager)
    {
        _storageService = storageService;
        _dbContextFactory = dbContextFactory;
    }

    public override string NodeType => "StorageClient";

    public override List<string> GetOutputParameters() => new()
    {
        "Records",        // List of matching records (for Query)
        "RecordsJson",    // JSON array of records
        "FirstRecord",    // First record as JSON
        "Count",          // Number of records returned
        "AffectedCount",  // Number of records affected (for Delete/Update)
        "InsertedId"      // ID of inserted/upserted record
    };

    /// <summary>
    /// Set the connected Storage Table node ID (resolved from storage connection).
    /// </summary>
    public void SetConnectedStorageTable(Guid? storageTableNodeId)
    {
        _connectedStorageTableNodeId = storageTableNodeId;
    }

    /// <summary>
    /// Set the user ID for record operations.
    /// </summary>
    public void SetUserId(string userId)
    {
        _userId = userId;
    }

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        _userId = userId;
        var config = JsonSerializer.Deserialize<StorageClientConfig>(node.Configuration ?? "{}") ?? new();
        var operation = config.Operation ?? "Query";

        // Resolve placeholders in configuration values
        var filterValue = ResolvePlaceholders(config.FilterValue ?? "", inputData);
        var dataJson = ResolvePlaceholders(config.DataJson ?? "{}", inputData);
        var recordIdStr = ResolvePlaceholders(config.RecordId ?? "", inputData);

        Log(node, NodeLogLevel.Info, $"Storage Client executing operation: {operation}");

        // Validate storage table connection
        if (!_connectedStorageTableNodeId.HasValue || _connectedStorageTableNodeId == Guid.Empty)
        {
            Log(node, NodeLogLevel.Error, "No Storage Table connected. Connect this node to a Storage Table using a 'storage' connection.");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "No Storage Table connected. Use a 'storage' connection to link to a Storage Table node."
            };
        }

        var storageTableNodeId = _connectedStorageTableNodeId.Value;

        // Auto-sync columns from the connected Storage Table's configuration
        // This is needed because Storage Table node may not have executed (storage connection is architectural)
        await SyncStorageTableColumnsAsync(storageTableNodeId);

        try
        {
            var outputData = new Dictionary<string, object?>();

            switch (operation.ToLower())
            {
                case "query":
                    await ExecuteQuery(node, storageTableNodeId, config, filterValue, outputData);
                    break;

                case "insert":
                    await ExecuteInsert(node, storageTableNodeId, dataJson, outputData);
                    break;

                case "upsert":
                    await ExecuteUpsert(node, storageTableNodeId, recordIdStr, dataJson, outputData);
                    break;

                case "update":
                    await ExecuteUpdate(node, storageTableNodeId, recordIdStr, dataJson, outputData);
                    break;

                case "delete":
                    await ExecuteDelete(node, storageTableNodeId, recordIdStr, outputData);
                    break;

                case "deletebyfilter":
                    await ExecuteDeleteByFilter(node, storageTableNodeId, config, filterValue, outputData);
                    break;

                default:
                    Log(node, NodeLogLevel.Error, $"Unknown operation: {operation}");
                    return new NodeExecutionResult
                    {
                        Success = false,
                        ErrorMessage = $"Unknown operation: {operation}"
                    };
            }

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = outputData
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Storage Client error: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task ExecuteQuery(
        WorkflowNode node, 
        Guid storageTableNodeId, 
        StorageClientConfig config, 
        string filterValue,
        Dictionary<string, object?> outputData)
    {
        Log(node, NodeLogLevel.Info, $"Querying table {storageTableNodeId} with filter: {config.FilterColumn} {config.FilterOperator} {filterValue}");
        
        var records = await _storageService.QueryRecordsAsync(
            storageTableNodeId,
            config.FilterColumn,
            config.FilterOperator,
            filterValue,
            config.MaxResults);

        Log(node, NodeLogLevel.Info, $"Raw query returned {records.Count} records from database");

        var recordsList = records.Select(r => {
            try
            {
                return new
                {
                    r.Id,
                    r.Timestamp,
                    Data = string.IsNullOrEmpty(r.DataJson) 
                        ? new Dictionary<string, object?>() 
                        : JsonSerializer.Deserialize<Dictionary<string, object?>>(r.DataJson) ?? new()
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageClient] Error parsing DataJson for record {r.Id}: {ex.Message}");
                return new
                {
                    r.Id,
                    r.Timestamp,
                    Data = new Dictionary<string, object?>()
                };
            }
        }).ToList();

        var recordsJson = JsonSerializer.Serialize(recordsList);
        Log(node, NodeLogLevel.Info, $"Serialized {recordsList.Count} records, JSON length: {recordsJson.Length}");

        outputData["Records"] = recordsList;
        outputData["RecordsJson"] = recordsJson;
        outputData["FirstRecord"] = recordsList.FirstOrDefault() != null 
            ? JsonSerializer.Serialize(recordsList.First()) 
            : null;
        outputData["Count"] = recordsList.Count;
        outputData["AffectedCount"] = 0;
        outputData["InsertedId"] = null;

        Log(node, NodeLogLevel.Info, $"Query returned {recordsList.Count} records");
    }

    private async Task ExecuteInsert(
        WorkflowNode node,
        Guid storageTableNodeId,
        string dataJson,
        Dictionary<string, object?> outputData)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson) ?? new();
        var (success, insertedId, error) = await _storageService.InsertRecordAsync(storageTableNodeId, _userId ?? "", data);

        if (!success)
        {
            throw new InvalidOperationException(error ?? "Storage limit exceeded");
        }

        outputData["Records"] = null;
        outputData["RecordsJson"] = "[]";
        outputData["FirstRecord"] = null;
        outputData["Count"] = 0;
        outputData["AffectedCount"] = 1;
        outputData["InsertedId"] = insertedId.ToString();

        Log(node, NodeLogLevel.Info, $"Inserted record with ID: {insertedId}");
    }

    private async Task ExecuteUpsert(
        WorkflowNode node,
        Guid storageTableNodeId,
        string recordIdStr,
        string dataJson,
        Dictionary<string, object?> outputData)
    {
        Guid? recordId = Guid.TryParse(recordIdStr, out var parsed) ? parsed : null;
        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson) ?? new();
        var (success, resultId, error) = await _storageService.UpsertRecordAsync(storageTableNodeId, _userId ?? "", recordId, data);

        if (!success)
        {
            throw new InvalidOperationException(error ?? "Storage limit exceeded");
        }

        outputData["Records"] = null;
        outputData["RecordsJson"] = "[]";
        outputData["FirstRecord"] = null;
        outputData["Count"] = 0;
        outputData["AffectedCount"] = 1;
        outputData["InsertedId"] = resultId.ToString();

        Log(node, NodeLogLevel.Info, $"Upserted record with ID: {resultId}");
    }

    private async Task ExecuteUpdate(
        WorkflowNode node,
        Guid storageTableNodeId,
        string recordIdStr,
        string dataJson,
        Dictionary<string, object?> outputData)
    {
        if (!Guid.TryParse(recordIdStr, out var recordId))
        {
            throw new ArgumentException("Record ID is required for Update operation");
        }

        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson) ?? new();
        var success = await _storageService.UpdateRecordAsync(recordId, storageTableNodeId, data);

        outputData["Records"] = null;
        outputData["RecordsJson"] = "[]";
        outputData["FirstRecord"] = null;
        outputData["Count"] = 0;
        outputData["AffectedCount"] = success ? 1 : 0;
        outputData["InsertedId"] = null;

        Log(node, success ? NodeLogLevel.Info : NodeLogLevel.Warning, 
            success ? $"Updated record: {recordId}" : $"Record not found: {recordId}");
    }

    private async Task ExecuteDelete(
        WorkflowNode node,
        Guid storageTableNodeId,
        string recordIdStr,
        Dictionary<string, object?> outputData)
    {
        if (!Guid.TryParse(recordIdStr, out var recordId))
        {
            throw new ArgumentException("Record ID is required for Delete operation");
        }

        var success = await _storageService.DeleteRecordAsync(recordId, storageTableNodeId);

        outputData["Records"] = null;
        outputData["RecordsJson"] = "[]";
        outputData["FirstRecord"] = null;
        outputData["Count"] = 0;
        outputData["AffectedCount"] = success ? 1 : 0;
        outputData["InsertedId"] = null;

        Log(node, success ? NodeLogLevel.Info : NodeLogLevel.Warning,
            success ? $"Deleted record: {recordId}" : $"Record not found: {recordId}");
    }

    private async Task ExecuteDeleteByFilter(
        WorkflowNode node,
        Guid storageTableNodeId,
        StorageClientConfig config,
        string filterValue,
        Dictionary<string, object?> outputData)
    {
        var deletedCount = await _storageService.DeleteRecordsAsync(
            storageTableNodeId,
            config.FilterColumn,
            config.FilterOperator,
            filterValue);

        outputData["Records"] = null;
        outputData["RecordsJson"] = "[]";
        outputData["FirstRecord"] = null;
        outputData["Count"] = 0;
        outputData["AffectedCount"] = deletedCount;
        outputData["InsertedId"] = null;

        Log(node, NodeLogLevel.Info, $"Deleted {deletedCount} records matching filter");
    }

    private string ResolvePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var result = template;
        var placeholderRegex = new System.Text.RegularExpressions.Regex(@"\{\{([^}]+)\}\}");
        result = placeholderRegex.Replace(result, match =>
        {
            var key = match.Groups[1].Value;

            // Try exact match first
            if (data.TryGetValue(key, out var value) && value != null)
                return value.ToString() ?? "";

            // Try without node prefix
            var shortKey = key.Contains('.') ? key.Split('.').Last() : key;
            if (data.TryGetValue(shortKey, out var shortValue) && shortValue != null)
                return shortValue.ToString() ?? "";

            // Try to find key in any prefixed format
            foreach (var kvp in data)
            {
                if (kvp.Key.EndsWith("." + key) || kvp.Key.EndsWith("." + shortKey))
                {
                    return kvp.Value?.ToString() ?? "";
                }
            }

            return match.Value; // Return original if not found
        });

        return result;
    }

    /// <summary>
    /// Sync columns from connected Storage Table's configuration to the database.
    /// This ensures columns are available even if the Storage Table node hasn't executed.
    /// </summary>
    private async Task SyncStorageTableColumnsAsync(Guid storageTableNodeId)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            
            // Get the Storage Table node's configuration from the database
            var tableNode = await context.WorkflowNodes
                .FirstOrDefaultAsync(n => n.Id == storageTableNodeId && n.NodeType == "StorageTable");
            
            if (tableNode == null || string.IsNullOrEmpty(tableNode.Configuration))
                return;
            
            // Parse the configuration to extract column definitions
            var config = JsonSerializer.Deserialize<StorageTableConfig>(tableNode.Configuration);
            if (config?.Columns == null || !config.Columns.Any())
                return;
            
            // Convert to StorageTableColumn entities
            var columns = config.Columns.Select((c, i) => new StorageTableColumn
            {
                Id = Guid.NewGuid(),
                StorageTableNodeId = storageTableNodeId,
                ColumnName = c.Name,
                ColumnType = c.Type,
                OrderIndex = i
            }).ToList();
            
            // Save columns (this will replace any existing columns)
            await _storageService.SaveColumnsAsync(storageTableNodeId, columns);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageClientNode] Failed to sync columns: {ex.Message}");
            // Don't fail the operation - columns might already exist
        }
    }
}

/// <summary>
/// Configuration for Storage Client node.
/// </summary>
public class StorageClientConfig
{
    public string Operation { get; set; } = "Query";
    public string? RecordId { get; set; }
    public string? FilterColumn { get; set; }
    public string? FilterOperator { get; set; }
    public string? FilterValue { get; set; }
    public string? DataJson { get; set; }
    public int MaxResults { get; set; } = 100;
}
