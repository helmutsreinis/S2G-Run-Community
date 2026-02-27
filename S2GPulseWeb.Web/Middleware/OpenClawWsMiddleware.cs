using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Middleware;

/// <summary>
/// ASP.NET Core middleware that handles WebSocket upgrade requests at
/// /api/openclaw/ws/{nodeId}
///
/// Validates the node exists, is an OpenClaw node that is running, then
/// creates an OpenClawWsSession and runs it until the connection closes.
/// </summary>
public class OpenClawWsMiddleware
{
    private const string WsPathPrefix = "/api/openclaw/ws/";
    private readonly RequestDelegate _next;
    private readonly ILogger<OpenClawWsMiddleware> _logger;

    public OpenClawWsMiddleware(RequestDelegate next, ILogger<OpenClawWsMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Only handle our WS path
        if (!context.WebSockets.IsWebSocketRequest ||
            !path.StartsWith(WsPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Extract nodeId
        var nodeIdStr = path[WsPathPrefix.Length..].Trim('/');
        if (!Guid.TryParse(nodeIdStr, out var nodeId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid node ID");
            return;
        }

        // Resolve singleton/scoped services
        var executionManager = context.RequestServices.GetRequiredService<Logic.NodeExecutionManager>();
        var sessionManager   = context.RequestServices.GetRequiredService<Logic.OpenClawWsSessionManager>();
        var scopeFactory     = context.RequestServices.GetRequiredService<IServiceScopeFactory>();

        // Verify the node exists and is an OpenClaw node
        using var scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var node = await db.WorkflowNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == nodeId && n.NodeType == "OpenClaw");

        if (node == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("OpenClaw node not found");
            return;
        }

        // Node must be running (armed)
        if (!executionManager.IsRunning(nodeId))
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsync("OpenClaw node is not running. Start the workflow first.");
            return;
        }

        // Read optional secret from node config
        var config = JsonSerializer.Deserialize<OpenClawBridgeConfig>(node.Configuration ?? "{}") ??
                     new OpenClawBridgeConfig();

        // Accept the WebSocket
        var ws = await context.WebSockets.AcceptWebSocketAsync();

        var sessionLogger = context.RequestServices.GetRequiredService<ILogger<Logic.OpenClawWsSession>>();
        var session = new Logic.OpenClawWsSession(ws, nodeId, config.TriggerSecret, scopeFactory, executionManager, sessionManager, sessionLogger);

        sessionManager.AddSession(nodeId, session);
        _logger.LogInformation("[OpenClawWS] New connection to node {NodeId}. Total: {Count}", nodeId, sessionManager.ConnectionCount(nodeId));

        try
        {
            // Use a linked token so disconnecting the workflow also closes the WS
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            await session.RunAsync(cts.Token);
        }
        finally
        {
            sessionManager.RemoveSession(nodeId, session);
            _logger.LogInformation("[OpenClawWS] Connection removed for node {NodeId}. Remaining: {Count}", nodeId, sessionManager.ConnectionCount(nodeId));

            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None); }
                catch { /* best-effort */ }
            }
        }
    }
}

/// <summary>Minimal config model read just for the WS middleware auth check.</summary>
internal class OpenClawBridgeConfig
{
    public string? TriggerSecret { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
}
