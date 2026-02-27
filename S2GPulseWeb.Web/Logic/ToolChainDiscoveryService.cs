using System;
using System.Collections.Generic;
using System.Linq;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic.Nodes;
using S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for discovering tool chains and agent connections from workflow canvas.
/// Uses BFS traversal to find all nodes in branching tool chains.
/// </summary>
public class ToolChainDiscoveryService
{
    /// <summary>
    /// Discovers all tool chains connected to an orchestrator node via "tool:*" labeled connections.
    /// </summary>
    public static List<ToolChainInfo> DiscoverToolChains(
        Guid orchestratorNodeId,
        List<WorkflowNode> nodes,
        List<WorkflowConnection> connections)
    {
        var toolChains = new List<ToolChainInfo>();

        // Find all connections with "tool:" prefix (check BOTH directions)
        // Either FROM orchestrator TO tool, OR FROM tool TO orchestrator
        var toolConnections = connections
            .Where(c => c.Label?.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) == true &&
                        (c.SourceNodeId == orchestratorNodeId || c.TargetNodeId == orchestratorNodeId))
            .ToList();

        foreach (var toolConn in toolConnections)
        {
            var toolTag = toolConn.Label ?? "tool:unknown";
            // Get the tool node (the one that's NOT the orchestrator)
            var toolNodeId = toolConn.SourceNodeId == orchestratorNodeId 
                ? toolConn.TargetNodeId 
                : toolConn.SourceNodeId;
            var entryNode = nodes.FirstOrDefault(n => n.Id == toolNodeId);
            
            if (entryNode == null) continue;

            var chainInfo = new ToolChainInfo
            {
                ToolTag = toolTag,
                EntryNodeId = entryNode.Id,
                EntryNodeType = entryNode.NodeType,
                Branches = new List<ToolChainBranch>()
            };

            // BFS to discover all nodes in this chain
            var visited = new HashSet<Guid>();
            var branchQueue = new Queue<(Guid nodeId, List<ChainNode> path)>();
            branchQueue.Enqueue((entryNode.Id, new List<ChainNode> 
            { 
                new ChainNode { NodeId = entryNode.Id, NodeName = entryNode.Name, NodeType = entryNode.NodeType }
            }));

            while (branchQueue.Count > 0)
            {
                var (currentId, currentPath) = branchQueue.Dequeue();
                
                if (visited.Contains(currentId))
                    continue;
                    
                visited.Add(currentId);

                // Find downstream connections from current node (excluding special labels)
                var downstream = connections
                    .Where(c => c.SourceNodeId == currentId &&
                                !string.Equals(c.Label, "reader", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(c.Label, "storage", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(c.Label, "agent", StringComparison.OrdinalIgnoreCase) &&
                                !c.Label?.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();

                if (downstream.Count == 0)
                {
                    // Terminal node - create a branch
                    var currentNode = nodes.FirstOrDefault(n => n.Id == currentId);
                    chainInfo.Branches.Add(new ToolChainBranch
                    {
                        Nodes = new List<ChainNode>(currentPath),
                        TerminalNodeId = currentId,
                        TerminalNodeType = currentNode?.NodeType ?? "Unknown",
                        AvailableOutputs = GetOutputParametersForNode(currentNode?.NodeType)
                    });
                }
                else
                {
                    // Continue traversal for each downstream connection
                    foreach (var conn in downstream)
                    {
                        var nextNode = nodes.FirstOrDefault(n => n.Id == conn.TargetNodeId);
                        if (nextNode == null) continue;

                        var newPath = new List<ChainNode>(currentPath)
                        {
                            new ChainNode { NodeId = nextNode.Id, NodeName = nextNode.Name, NodeType = nextNode.NodeType }
                        };
                        branchQueue.Enqueue((nextNode.Id, newPath));
                    }
                }
            }

            chainInfo.BranchCount = chainInfo.Branches.Count;
            chainInfo.TotalNodeCount = visited.Count;
            toolChains.Add(chainInfo);
        }

        return toolChains;
    }

    /// <summary>
    /// Discovers all agent nodes connected to an orchestrator via "agent" labeled connections.
    /// </summary>
    public static List<ConnectedAgent> DiscoverAgents(
        Guid orchestratorNodeId,
        List<WorkflowNode> nodes,
        List<WorkflowConnection> connections)
    {
        var agents = new List<ConnectedAgent>();

        // Find all connections TO the orchestrator with "agent" label
        var agentConnections = connections
            .Where(c => c.TargetNodeId == orchestratorNodeId && 
                        string.Equals(c.Label, "agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var order = 0;
        foreach (var agentConn in agentConnections)
        {
            var agentNode = nodes.FirstOrDefault(n => n.Id == agentConn.SourceNodeId);
            if (agentNode == null) continue;

            // Only accept AI nodes as agents
            if (!IsAINode(agentNode.NodeType)) continue;

            agents.Add(new ConnectedAgent
            {
                NodeId = agentNode.Id,
                RoleName = agentNode.Name,
                NodeType = agentNode.NodeType,
                SystemPrompt = "", // Will be populated from node's config
                AssignedTools = new List<string>(),
                ExecutionOrder = order++
            });
        }

        return agents;
    }

    /// <summary>
    /// Gets tool chains connected TO a specific agent node via "tool:*" connections.
    /// </summary>
    public static List<string> GetAgentAssignedTools(
        Guid agentNodeId,
        List<WorkflowConnection> connections)
    {
        return connections
            .Where(c => c.SourceNodeId == agentNodeId && 
                        c.Label?.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) == true)
            .Select(c => c.Label!)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Checks if a node type is an AI provider node.
    /// </summary>
    private static bool IsAINode(string nodeType)
    {
        return nodeType switch
        {
            "Anthropic" => true,
            "OpenAI" => true,
            "Mistral" => true,
            "Groq" => true,
            "Gemini" => true,
            "DeepSeek" => true,
            _ => false
        };
    }

    /// <summary>
    /// Gets the output parameters for a node type.
    /// </summary>
    private static List<string> GetOutputParametersForNode(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return new List<string>();

        return NodeHelper.GetOutputParametersForType(nodeType);
    }
}
