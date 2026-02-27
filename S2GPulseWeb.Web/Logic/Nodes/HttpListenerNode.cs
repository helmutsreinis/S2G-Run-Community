using System.Collections.Generic;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class HttpListenerNode : BaseNodeExecutor
{
    public HttpListenerNode(NodeExecutionManager executionManager) : base(executionManager)
    {
    }

    public override string NodeType => "HttpListener";

    public override List<string> GetOutputParameters() => new() { "QueryParams", "Headers", "Body", "Method", "Path" };

    protected override Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        if (_executionManager.IsRunning(node.Id))
        {
            _executionManager.StopNode(node.Id);
            Log(node, NodeLogLevel.Info, $"Stopped HTTP Listener for {node.Name}");
            node.Status = NodeStatus.Idle;
            return Task.FromResult(new NodeExecutionResult { Success = true });
        }
        else
        {
            try
            {
                var config = System.Text.Json.JsonSerializer.Deserialize<HttpListenerConfig>(node.Configuration ?? "{}") ?? new();

                // Check for proxy mode - no HTTP binding needed, just mark as ready
                if (config.UseProxyMode)
                {
                    Log(node, NodeLogLevel.Info, $"Started HTTP Listener for {node.Name} (Proxy Mode)", 
                        $"Node ID: {node.Id}\nRequests will be routed via Azure Function proxy.\nNo direct HTTP binding required.");
                    node.Status = NodeStatus.Running;
                    
                    // Register a "dummy" execution so IsRunning returns true and the node can be stopped
                    _executionManager.RegisterActiveExecution(node.Id, new ProxyModeExecution());
                    
                    return Task.FromResult(new NodeExecutionResult { Success = true });
                }

                // Direct HTTP binding mode (for local development)
                var host = string.IsNullOrWhiteSpace(config.Host) ? "localhost" : config.Host;
                var respBody = string.IsNullOrWhiteSpace(config.DefaultResponse) ? "OK" : config.DefaultResponse;
                var contentType = string.IsNullOrWhiteSpace(config.ContentType) ? "text/plain" : config.ContentType;
                var statusCode = config.DefaultStatusCode == 0 ? 200 : config.DefaultStatusCode;
                var timeoutSeconds = config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300; // Default 5 minutes

                _executionManager.StartHttpListener(
                    node.Id, 
                    config.Port, 
                    config.Path ?? "/api", 
                    config.Method ?? "GET",
                    host,
                    respBody,
                    contentType,
                    statusCode,
                    timeoutSeconds,
                    (data) =>
                    {
                        var detail = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        Log(node, NodeLogLevel.Info, $"Received {data["Method"]} request to {data["Path"]}", detail);
                    });

                Log(node, NodeLogLevel.Info, $"Started real HTTP Listener for {node.Name} on {host}:{config.Port}");
                node.Status = NodeStatus.Running;
                return Task.FromResult(new NodeExecutionResult { Success = true });
            }
            catch (Exception ex)
            {
                Log(node, NodeLogLevel.Error, $"Failed to start HTTP Listener: {ex.Message}");
                return Task.FromResult(new NodeExecutionResult { Success = false, ErrorMessage = ex.Message });
            }
        }
    }

    /// <summary>
    /// Dummy execution class for proxy mode - allows the node to be stopped via IsRunning/StopNode
    /// </summary>
    private class ProxyModeExecution : IDisposable
    {
        public void Dispose()
        {
            // Nothing to clean up in proxy mode
        }
    }

    private class HttpListenerConfig
    {
        public string? Method { get; set; }
        public string? Path { get; set; }
        public int Port { get; set; }
        public string? Host { get; set; }
        public string? DefaultResponse { get; set; }
        public string? ContentType { get; set; }
        public int DefaultStatusCode { get; set; }
        /// <summary>Response timeout in seconds (default 300 = 5 minutes, for long AI operations)</summary>
        public int TimeoutSeconds { get; set; } = 300;
        /// <summary>JSON body sample from last request for dynamic property detection</summary>
        public string? LastBodySample { get; set; }
        /// <summary>Query parameter names from last request (comma-separated)</summary>
        public string? LastQueryParams { get; set; }
        /// <summary>
        /// When true, node receives requests via Azure Function proxy instead of direct HTTP binding.
        /// Default is true - proxy mode is the primary mode for containerized deployments.
        /// </summary>
        public bool UseProxyMode { get; set; } = true;
    }
}

