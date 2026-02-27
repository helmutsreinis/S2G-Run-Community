using System.Text.Json;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Vector Client node for performing operations against a connected Vector Store.
/// Supports: Store, Search, Delete, Clear operations.
/// Must be connected to a VectorDb node via "storage" connection.
/// </summary>
public class VectorClientNode : BaseNodeExecutor
{
    private readonly VectorDbService _vectorDbService;
    private Guid? _connectedStoreId;
    private string? _userId;

    public VectorClientNode(NodeExecutionManager executionManager, VectorDbService vectorDbService)
        : base(executionManager)
    {
        _vectorDbService = vectorDbService;
    }

    public override string NodeType => "VectorClient";

    public override List<string> GetOutputParameters() => new()
    {
        "Results",          // JSON array of search results
        "ResultsJson",      // Same as Results
        "FirstResult",      // Top match text
        "TopSimilarity",    // Highest similarity score
        "Count",            // Number of results or stored documents
        "InsertedId",       // ID of newly stored document
        "OperationResult"   // Success/failure message
    };

    /// <summary>
    /// Set the connected Vector Store node ID (resolved from storage connection).
    /// </summary>
    public void SetConnectedVectorStore(Guid storeNodeId)
    {
        _connectedStoreId = storeNodeId;
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
        
        if (!_connectedStoreId.HasValue)
        {
            Log(node, NodeLogLevel.Error, "VectorClient requires a connection to a Vector Store node");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "No Vector Store connected. Add a 'storage' connection from this node to a Vector Store node."
            };
        }

        var config = JsonSerializer.Deserialize<VectorClientConfig>(node.Configuration ?? "{}") ?? new();
        
        Log(node, NodeLogLevel.Info, $"VectorClient executing operation: {config.Operation} (Store: {_connectedStoreId.Value.ToString().Substring(0, 8)}...)");

        try
        {
            return config.Operation switch
            {
                "Store" => await ExecuteStore(node, config, inputData),
                "Search" => await ExecuteSearch(node, config, inputData),
                "Delete" => await ExecuteDelete(node, config, inputData),
                "Clear" => await ExecuteClear(node),
                _ => throw new ArgumentException($"Unknown operation: {config.Operation}")
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"VectorClient error: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<NodeExecutionResult> ExecuteStore(
        WorkflowNode node,
        VectorClientConfig config,
        Dictionary<string, object?> inputData)
    {
        var text = ResolvePlaceholder(config.Text, inputData);
        if (string.IsNullOrWhiteSpace(text))
        {
            Log(node, NodeLogLevel.Error, "Store operation requires Text");
            return new NodeExecutionResult { Success = false, ErrorMessage = "Text is required for Store operation" };
        }

        var embeddingJson = ResolvePlaceholder(config.Embedding, inputData);
        float[] embedding;
        try
        {
            embedding = ParseEmbedding(embeddingJson);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Failed to parse embedding: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Invalid embedding format: {ex.Message}" };
        }

        var metadata = string.IsNullOrWhiteSpace(config.Metadata) ? null : ResolvePlaceholder(config.Metadata, inputData);

        var (success, documentId, error) = await _vectorDbService.StoreAsync(_connectedStoreId!.Value, _userId ?? "", text, embedding, metadata);
        
        if (!success)
        {
            Log(node, NodeLogLevel.Error, error ?? "Storage limit exceeded");
            return new NodeExecutionResult { Success = false, ErrorMessage = error ?? "Storage limit exceeded" };
        }
        
        var count = await _vectorDbService.GetCountAsync(_connectedStoreId!.Value);

        Log(node, NodeLogLevel.Info, $"Stored document with {embedding.Length} dimensions. Total documents: {count}");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "InsertedId", documentId.ToString() },
                { "Count", count },
                { "OperationResult", "Document stored successfully" }
            }
        };
    }

    private async Task<NodeExecutionResult> ExecuteSearch(
        WorkflowNode node,
        VectorClientConfig config,
        Dictionary<string, object?> inputData)
    {
        var queryEmbeddingJson = ResolvePlaceholder(config.QueryEmbedding, inputData);
        float[] queryVector;
        try
        {
            queryVector = ParseEmbedding(queryEmbeddingJson);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Failed to parse query embedding: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Invalid query embedding format: {ex.Message}" };
        }

        var limit = config.Limit > 0 ? config.Limit : 3;
        var results = await _vectorDbService.SearchAsync(_connectedStoreId!.Value, queryVector, limit);

        Log(node, NodeLogLevel.Info, $"Search returned {results.Count} results");

        var resultsJson = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        var firstResult = results.FirstOrDefault()?.Text ?? "";
        var topSimilarity = results.FirstOrDefault()?.Similarity ?? 0f;

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Results", resultsJson },
                { "ResultsJson", resultsJson },
                { "FirstResult", firstResult },
                { "TopSimilarity", topSimilarity },
                { "Count", results.Count },
                { "OperationResult", $"Found {results.Count} similar documents" }
            }
        };
    }

    private async Task<NodeExecutionResult> ExecuteDelete(
        WorkflowNode node,
        VectorClientConfig config,
        Dictionary<string, object?> inputData)
    {
        var documentIdStr = ResolvePlaceholder(config.DocumentId, inputData);
        if (!Guid.TryParse(documentIdStr, out var documentId))
        {
            Log(node, NodeLogLevel.Error, "Delete operation requires a valid DocumentId");
            return new NodeExecutionResult { Success = false, ErrorMessage = "Invalid DocumentId" };
        }

        var deleted = await _vectorDbService.DeleteAsync(documentId);
        Log(node, NodeLogLevel.Info, deleted ? $"Deleted document {documentId}" : $"Document {documentId} not found");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Count", deleted ? 1 : 0 },
                { "OperationResult", deleted ? "Document deleted" : "Document not found" }
            }
        };
    }

    private async Task<NodeExecutionResult> ExecuteClear(WorkflowNode node)
    {
        var deleted = await _vectorDbService.ClearAsync(_connectedStoreId!.Value);
        Log(node, NodeLogLevel.Info, $"Cleared {deleted} documents from vector store");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Count", deleted },
                { "OperationResult", $"Cleared {deleted} documents" }
            }
        };
    }

    private static float[] ParseEmbedding(string? embeddingJson)
    {
        if (string.IsNullOrWhiteSpace(embeddingJson))
            throw new ArgumentException("Embedding is required");

        try
        {
            return JsonSerializer.Deserialize<float[]>(embeddingJson)
                   ?? throw new ArgumentException("Failed to parse embedding as float array");
        }
        catch (JsonException)
        {
            var parts = embeddingJson.Trim('[', ']').Split(',');
            return parts.Select(p => float.Parse(p.Trim())).ToArray();
        }
    }

    private static string ResolvePlaceholder(string? value, Dictionary<string, object?> inputData)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var result = value;
        foreach (var kvp in inputData)
        {
            result = result.Replace($"{{{{{kvp.Key}}}}}", kvp.Value?.ToString() ?? "");
        }
        return result;
    }
}

/// <summary>
/// Configuration for VectorClient node.
/// </summary>
public class VectorClientConfig
{
    public string Operation { get; set; } = "Search";
    
    // Store operation
    public string? Text { get; set; }
    public string? Embedding { get; set; }
    public string? Metadata { get; set; }
    
    // Search operation
    public string? QueryEmbedding { get; set; }
    public int Limit { get; set; } = 3;
    
    // Delete operation
    public string? DocumentId { get; set; }
}
