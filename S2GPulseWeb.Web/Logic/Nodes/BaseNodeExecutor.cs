using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public abstract class BaseNodeExecutor : INodeExecutor
{
    protected readonly NodeExecutionManager _executionManager;

    protected BaseNodeExecutor() { _executionManager = null!; }
    
    protected BaseNodeExecutor(NodeExecutionManager executionManager)
    {
        _executionManager = executionManager;
    }

    public abstract string NodeType { get; }
    public abstract List<string> GetOutputParameters();

    public async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        node.Status = NodeStatus.Running;
        Log(node, NodeLogLevel.Info, $"Starting execution of {node.Name}");

        try
        {
            var result = await InternalExecuteAsync(node, inputData, userId);
            
            if (result.Success)
            {
                node.Status = NodeStatus.Success;
                Log(node, NodeLogLevel.Info, $"Execution of {node.Name} completed successfully");
            }
            else
            {
                node.Status = NodeStatus.Failure;
                Log(node, NodeLogLevel.Error, $"Execution of {node.Name} failed: {result.ErrorMessage}");
            }

            return result;
        }
        catch (Exception ex)
        {
            node.Status = NodeStatus.Failure;
            Log(node, NodeLogLevel.Error, $"Execution of {node.Name} encountered an exception: {ex.Message}", ex.StackTrace);
            return new NodeExecutionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    protected abstract Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId);

    protected void Log(WorkflowNode node, NodeLogLevel level, string message, string? detail = null)
    {
        var logEntry = new NodeLogEntry
        {
            NodeId = node.Id,
            Level = level,
            Message = message,
            Detail = detail,
            Timestamp = DateTime.UtcNow
        };

        // If execution manager is available, use event-based logging (UI subscribes to this)
        // The UI event handler will add the log to CanvasNode.ActivityLogs
        if (_executionManager != null)
        {
            _executionManager.AddNodeLog(node.Id, level, message, detail);
        }
        else
        {
            // Fallback: directly add to ActivityLogs when no execution manager
            node.ActivityLogs.Add(logEntry);
        }
    }
}
