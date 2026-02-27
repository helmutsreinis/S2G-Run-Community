using System.Text.Json;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Storage Table node for defining a persistent data table with typed columns.
/// This node defines the schema (columns) and retention settings.
/// </summary>
public class StorageTableNode : BaseNodeExecutor
{
    private readonly StorageTableService _storageService;

    public StorageTableNode(NodeExecutionManager executionManager, StorageTableService storageService) 
        : base(executionManager)
    {
        _storageService = storageService;
    }

    public override string NodeType => "StorageTable";

    public override List<string> GetOutputParameters() => new()
    {
        "ColumnsJson",    // JSON array of column definitions
        "RecordCount",    // Number of records in the table
        "TableNodeId"     // The node ID (for reference by Storage Client)
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<StorageTableConfig>(node.Configuration ?? "{}") ?? new();
        
        Log(node, NodeLogLevel.Info, $"Storage Table executing with {config.Columns.Count} columns defined");

        try
        {
            // Save column schema to database
            var columns = config.Columns.Select((c, i) => new StorageTableColumn
            {
                Id = Guid.NewGuid(),
                StorageTableNodeId = node.Id,
                ColumnName = c.Name,
                ColumnType = c.Type,
                OrderIndex = i
            }).ToList();

            await _storageService.SaveColumnsAsync(node.Id, columns);
            Log(node, NodeLogLevel.Info, $"Saved {columns.Count} column definitions");

            // Apply retention if enabled
            if (config.EnableRetention && config.RetentionDays > 0)
            {
                var deletedCount = await _storageService.ApplyRetentionAsync(node.Id, config.RetentionDays);
                if (deletedCount > 0)
                {
                    Log(node, NodeLogLevel.Info, $"Retention applied: deleted {deletedCount} records older than {config.RetentionDays} days");
                }
            }

            // Get record count
            var recordCount = await _storageService.GetRecordCountAsync(node.Id);

            var outputData = new Dictionary<string, object?>
            {
                { "ColumnsJson", JsonSerializer.Serialize(config.Columns) },
                { "RecordCount", recordCount },
                { "TableNodeId", node.Id.ToString() }
            };

            Log(node, NodeLogLevel.Info, $"Storage Table ready. Columns: {config.Columns.Count}, Records: {recordCount}");

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = outputData
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Storage Table error: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}

/// <summary>
/// Configuration for Storage Table node.
/// </summary>
public class StorageTableConfig
{
    public List<StorageTableColumnDef> Columns { get; set; } = new();
    public bool EnableRetention { get; set; } = false;
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// Column definition for Storage Table configuration.
/// </summary>
public class StorageTableColumnDef
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "String";
}
