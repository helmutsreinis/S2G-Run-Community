using System.Text.Json;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Vector Store node for defining a vector database store.
/// This node acts as the "schema" that VectorClient nodes connect to via "storage" connections.
/// Similar to StorageTable for Storage pattern.
/// </summary>
public class VectorDbNode : BaseNodeExecutor
{
    private readonly VectorDbService _vectorDbService;

    public VectorDbNode(NodeExecutionManager executionManager, VectorDbService vectorDbService)
        : base(executionManager)
    {
        _vectorDbService = vectorDbService;
    }

    public override string NodeType => "VectorDb";

    public override List<string> GetOutputParameters() => new()
    {
        "DocumentCount",  // Number of documents in the store
        "StoreNodeId"     // The node ID (for reference by VectorClient)
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        Log(node, NodeLogLevel.Info, "Vector Store initialized");

        try
        {
            // Get document count
            var documentCount = await _vectorDbService.GetCountAsync(node.Id);

            Log(node, NodeLogLevel.Info, $"Vector Store ready. Documents: {documentCount}");

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "DocumentCount", documentCount },
                    { "StoreNodeId", node.Id.ToString() }
                }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Vector Store error: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
