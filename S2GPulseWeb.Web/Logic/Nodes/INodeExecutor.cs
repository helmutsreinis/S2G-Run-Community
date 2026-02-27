using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public interface INodeExecutor
{
    string NodeType { get; }
    Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId);
    List<string> GetOutputParameters();
}
