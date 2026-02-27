using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

public class HttpResponseData
{
    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "text/plain";
    public Dictionary<string, string> Headers { get; set; } = new();
}

public class NodeExecutionManager
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<HttpResponseData>> _pendingRequests = new();
    private readonly ConcurrentDictionary<Guid, NodeStatus> _nodeStatuses = new();
    private readonly ConcurrentDictionary<Guid, IDisposable> _activeExecutions = new();

    public event Action<Guid, NodeLogEntry>? OnNodeLogAdded;
    public event Action<Guid, Dictionary<string, object?>>? OnNodeOutputDataReceived;
    /// <summary>
    /// Fired when a node's configuration is updated with sample data (e.g., from listener body detection).
    /// Parameters: nodeId, newConfiguration
    /// </summary>
    public event Action<Guid, string>? OnNodeConfigurationUpdated;

    public NodeStatus GetNodeStatus(Guid nodeId)
    {
        return _nodeStatuses.TryGetValue(nodeId, out var status) ? status : NodeStatus.Idle;
    }

    public void SetNodeStatus(Guid nodeId, NodeStatus status)
    {
        _nodeStatuses[nodeId] = status;
    }

    public void RegisterActiveExecution(Guid nodeId, IDisposable execution)
    {
        _activeExecutions[nodeId] = execution;
        _nodeStatuses[nodeId] = NodeStatus.Running;
    }

    public void StopNode(Guid nodeId)
    {
        if (_activeExecutions.TryRemove(nodeId, out var execution))
        {
            execution.Dispose();
            _nodeStatuses[nodeId] = NodeStatus.Idle;
        }
    }

    public bool IsRunning(Guid nodeId)
    {
        return GetNodeStatus(nodeId) == NodeStatus.Running;
    }

    /// <summary>
    /// Notify subscribers that a node's configuration has been updated
    /// </summary>
    public void NotifyConfigurationUpdated(Guid nodeId, string newConfiguration)
    {
        OnNodeConfigurationUpdated?.Invoke(nodeId, newConfiguration);
    }

    /// <summary>
    /// Add a log entry for a node and notify subscribers (UI).
    /// Use this instead of BaseNodeExecutor.Log for trigger/polling nodes.
    /// </summary>
    public void AddNodeLog(Guid nodeId, NodeLogLevel level, string message, string? detail = null)
    {
        var logEntry = new NodeLogEntry
        {
            NodeId = nodeId,
            Level = level,
            Message = message,
            Detail = detail,
            Timestamp = DateTime.UtcNow
        };
        OnNodeLogAdded?.Invoke(nodeId, logEntry);
    }

    public void EmitResponse(Guid requestId, HttpResponseData response)
    {
        Console.WriteLine($"[NodeExecutionManager] EmitResponse called for request {requestId}. Pending requests: {_pendingRequests.Count}");
        if (_pendingRequests.TryRemove(requestId, out var tcs))
        {
            Console.WriteLine($"[NodeExecutionManager] Found pending request {requestId}, completing with status {response.StatusCode}");
            tcs.TrySetResult(response);
        }
        else
        {
            Console.WriteLine($"[NodeExecutionManager] Request {requestId} NOT FOUND in pending requests! Keys: {string.Join(", ", _pendingRequests.Keys)}");
        }
    }

    /// <summary>
    /// Register a pending request from the proxy API (for Azure Function routing).
    /// This allows the ListenerProxyController to wait for workflow completion.
    /// </summary>
    public void RegisterPendingRequest(Guid requestId, TaskCompletionSource<HttpResponseData> tcs)
    {
        Console.WriteLine($"[NodeExecutionManager] RegisterPendingRequest: {requestId}. Total pending: {_pendingRequests.Count + 1}");
        _pendingRequests[requestId] = tcs;
    }

    public void StartHttpListener(Guid nodeId, int port, string path, string method, string host, string responseBody, string contentType, int defaultStatusCode, int timeoutSeconds, Action<Dictionary<string, object?>> onRequestReceived)
    {
        var listener = new System.Net.HttpListener();
        var prefix = $"http://{host}:{port}{path.TrimEnd('/')}/";
        if (!prefix.EndsWith("/")) prefix += "/";
        
        try
        {
            listener.Prefixes.Add(prefix);
            listener.Start();

            var cts = new System.Threading.CancellationTokenSource();
            var task = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested && listener.IsListening)
                {
                    try
                    {
                        var context = await listener.GetContextAsync();
                        var request = context.Request;

                        if (request.HttpMethod.Equals(method, StringComparison.OrdinalIgnoreCase))
                        {
                            var requestId = Guid.NewGuid();
                            var tcs = new TaskCompletionSource<HttpResponseData>();
                            _pendingRequests[requestId] = tcs;

                            var data = new Dictionary<string, object?>();
                            data["RequestId"] = requestId;
                            
                            // Query Params - parse and add as both dictionary and individual keys
                            var query = new Dictionary<string, string>();
                            foreach (string? key in request.QueryString.AllKeys)
                            {
                                if (key != null) 
                                {
                                    var value = request.QueryString[key] ?? "";
                                    query[key] = value;
                                    // Also add as individual output key for direct placeholder access
                                    data[key] = value;
                                }
                            }
                            data["QueryParams"] = query;
                            data["QueryParamsJson"] = System.Text.Json.JsonSerializer.Serialize(query);

                            // Headers
                            var headers = new Dictionary<string, string>();
                            foreach (string? key in request.Headers.AllKeys)
                            {
                                if (key != null) headers[key] = request.Headers[key] ?? "";
                            }
                            data["Headers"] = headers;
                            data["HeadersJson"] = System.Text.Json.JsonSerializer.Serialize(headers);

                            data["Method"] = request.HttpMethod;
                            data["Path"] = request.Url?.AbsolutePath ?? "";

                            // Extract body if needed
                            if (request.HasEntityBody)
                            {
                                using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding ?? System.Text.Encoding.UTF8);
                                var body = await reader.ReadToEndAsync();
                                data["Body"] = body;
                                data["_BodySample"] = body; // For config update with sample
                            }
                            
                            // Store query param names for config update
                            data["_QueryParamsSample"] = string.Join(",", query.Keys);

                            onRequestReceived(data);
                            
                            // Notify workflow service to trigger downstream nodes
                            OnNodeOutputDataReceived?.Invoke(nodeId, data);

                            // Note: Log is handled by onRequestReceived callback in HttpListenerNode
                            // to avoid duplicate log entries

                            // Wait for response from downstream or timeout
                            HttpResponseData finalResponse;
                            var timeoutMs = timeoutSeconds > 0 ? timeoutSeconds * 1000 : 300000; // Default 5 minutes
                            var timeoutTask = Task.Delay(timeoutMs);
                            if (await Task.WhenAny(tcs.Task, timeoutTask) == tcs.Task)
                            {
                                finalResponse = await tcs.Task;
                            }
                            else
                            {
                                _pendingRequests.TryRemove(requestId, out _);
                                finalResponse = new HttpResponseData 
                                { 
                                    StatusCode = defaultStatusCode, 
                                    Body = responseBody, 
                                    ContentType = contentType 
                                };
                            }

                            // Send Response
                            context.Response.StatusCode = finalResponse.StatusCode;
                            context.Response.ContentType = finalResponse.ContentType;
                            
                            foreach (var header in finalResponse.Headers)
                            {
                                context.Response.Headers.Add(header.Key, header.Value);
                            }

                            using var writer = new System.IO.StreamWriter(context.Response.OutputStream);
                            await writer.WriteAsync(finalResponse.Body);
                        }
                        else
                        {
                            context.Response.StatusCode = 405; // Method Not Allowed
                        }
                        context.Response.Close();
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception) { /* Handle or log listener errors */ }
                }
            }, cts.Token);

            var execution = new HttpListenerExecution(listener, cts);
            RegisterActiveExecution(nodeId, execution);
        }
        catch (Exception ex)
        {
            SetNodeStatus(nodeId, NodeStatus.Failure);
            throw new Exception($"Failed to start HTTP listener on {prefix}: {ex.Message}", ex);
        }
    }

    private class HttpListenerExecution : IDisposable
    {
        private readonly System.Net.HttpListener _listener;
        private readonly System.Threading.CancellationTokenSource _cts;

        public HttpListenerExecution(System.Net.HttpListener listener, System.Threading.CancellationTokenSource cts)
        {
            _listener = listener;
            _cts = cts;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Starts a polling trigger that executes a callback at regular intervals.
    /// </summary>
    public void StartPollingTrigger(Guid nodeId, int intervalMs, Func<Task> pollCallback)
    {
        var cts = new CancellationTokenSource();
        var timer = new System.Timers.Timer(intervalMs);
        
        timer.Elapsed += async (sender, args) =>
        {
            if (cts.Token.IsCancellationRequested) return;
            
            try
            {
                await pollCallback();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Polling error for node {nodeId}: {ex.Message}");
            }
        };
        
        timer.AutoReset = true;
        timer.Start();
        
        // Run initial poll immediately
        Task.Run(async () =>
        {
            try
            {
                await pollCallback();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Initial polling error for node {nodeId}: {ex.Message}");
            }
        });
        
        var execution = new PollingExecution(timer, cts);
        RegisterActiveExecution(nodeId, execution);
    }

    /// <summary>
    /// Triggers downstream node execution with the provided data.
    /// </summary>
    public void TriggerNodeExecution(Guid nodeId, Dictionary<string, object?> data)
    {
        OnNodeOutputDataReceived?.Invoke(nodeId, data);
    }

    private class PollingExecution : IDisposable
    {
        private readonly System.Timers.Timer _timer;
        private readonly CancellationTokenSource _cts;

        public PollingExecution(System.Timers.Timer timer, CancellationTokenSource cts)
        {
            _timer = timer;
            _cts = cts;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _timer.Stop();
            _timer.Dispose();
            _cts.Dispose();
        }
    }
}
