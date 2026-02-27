using System.Data;
using System.Text.Json;
using Npgsql;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class PostgresNode : BaseNodeExecutor
{
    public PostgresNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Postgresql";

    public override List<string> GetOutputParameters() => new() { "Rows", "Count" };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<SqlNodeConfig>(node.Configuration ?? "{}") ?? new();
        
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            return new NodeExecutionResult { Success = false, ErrorMessage = "Connection string is missing" };
        }

        using var connection = new NpgsqlConnection(config.ConnectionString);
        await connection.OpenAsync();

        Log(node, NodeLogLevel.Info, $"Connected to PostgreSQL. Executing query: {config.Query}");

        using var command = new NpgsqlCommand(config.Query, connection);
        
        foreach (var input in inputData)
        {
            if (config.Query?.Contains($":{input.Key}") == true || config.Query?.Contains($"@{input.Key}") == true)
            {
                command.Parameters.AddWithValue(input.Key, input.Value ?? DBNull.Value);
            }
        }

        using var reader = await command.ExecuteReaderAsync();
        var results = new List<Dictionary<string, object?>>();
        
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }

        Log(node, NodeLogLevel.Info, $"Query executed. {results.Count} rows returned.");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Rows", results },
                { "Count", results.Count }
            }
        };
    }
}
