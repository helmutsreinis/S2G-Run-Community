using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class MongoDbNode : BaseNodeExecutor
{
    public MongoDbNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "MongoDB";

    public override List<string> GetOutputParameters() => new() { "Result" };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<MongoDbConfig>(node.Configuration ?? "{}") ?? new();
        
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            return new NodeExecutionResult { Success = false, ErrorMessage = "Connection string is missing" };
        }

        var client = new MongoClient(config.ConnectionString);
        var database = client.GetDatabase(config.Database);
        var collection = database.GetCollection<BsonDocument>(config.Collection);

        Log(node, NodeLogLevel.Info, $"Connected to MongoDB. Operation: {config.Operation}");

        object? resultData = null;

        switch (config.Operation?.ToLower())
        {
            case "find":
                var filter = string.IsNullOrEmpty(config.Filter) ? new BsonDocument() : BsonDocument.Parse(config.Filter);
                var documents = await collection.Find(filter).ToListAsync();
                resultData = documents.Select(d => d.ToString()).ToList(); // Simplified output
                Log(node, NodeLogLevel.Info, $"Found {documents.Count} documents");
                break;
                
            case "insert":
                var docToInsert = BsonDocument.Parse(config.Document ?? "{}");
                await collection.InsertOneAsync(docToInsert);
                resultData = "Inserted successfully";
                Log(node, NodeLogLevel.Info, "Document inserted successfully");
                break;

            default:
                return new NodeExecutionResult { Success = false, ErrorMessage = $"Unsupported operation: {config.Operation}" };
        }

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Result", resultData }
            }
        };
    }
}

public class MongoDbConfig
{
    public string? ConnectionString { get; set; }
    public string? Database { get; set; }
    public string? Collection { get; set; }
    public string? Operation { get; set; } // find, insert, update, delete
    public string? Filter { get; set; }
    public string? Document { get; set; }
}
