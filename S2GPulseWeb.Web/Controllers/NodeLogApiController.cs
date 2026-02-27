using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// REST API for reading node execution logs and managing logging settings.
/// </summary>
[ApiController]
[Route("api/v1/workflows/{workflowId:guid}/nodes/{nodeId:guid}")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class NodeLogApiController : ControllerBase
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public NodeLogApiController(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>Get paginated logs for a specific node.</summary>
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs(
        Guid workflowId, Guid nodeId,
        [FromQuery] string? level = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var userId = GetUserId();
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Verify ownership
        var workflow = await context.Workflows
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.OwnerId == userId);
        if (workflow == null) return NotFound(new { error = "Workflow not found." });

        var query = context.NodeLogs
            .Where(l => l.WorkflowId == workflowId && l.NodeId == nodeId);

        if (!string.IsNullOrEmpty(level) && Enum.TryParse<NodeLogLevel>(level, true, out var logLevel))
            query = query.Where(l => l.Level == logLevel);

        if (dateFrom.HasValue)
            query = query.Where(l => l.Timestamp >= dateFrom.Value);

        if (dateTo.HasValue)
            query = query.Where(l => l.Timestamp <= dateTo.Value);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(l => l.Message.Contains(search));

        var totalCount = await query.CountAsync();
        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.NodeName,
                l.NodeType,
                l.Timestamp,
                Level = l.Level.ToString(),
                l.Message,
                l.Detail
            })
            .ToListAsync();

        return Ok(new
        {
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
            logs
        });
    }

    /// <summary>Get logging settings for a node.</summary>
    [HttpGet("logging-settings")]
    public async Task<IActionResult> GetLoggingSettings(Guid workflowId, Guid nodeId)
    {
        var userId = GetUserId();
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var node = await context.WorkflowNodes
            .FirstOrDefaultAsync(n => n.Id == nodeId && n.WorkflowId == workflowId &&
                                     context.Workflows.Any(w => w.Id == workflowId && w.OwnerId == userId));

        if (node == null) return NotFound(new { error = "Node not found." });

        return Ok(new
        {
            nodeId = node.Id,
            nodeName = node.Name,
            settings = node.LoggingSettingsJson ?? "{\"LoggingEnabled\":false,\"LogInfo\":true,\"LogWarning\":true,\"LogError\":true,\"LogDebug\":false}"
        });
    }

    /// <summary>Update logging settings for a node.</summary>
    [HttpPut("logging-settings")]
    public async Task<IActionResult> UpdateLoggingSettings(
        Guid workflowId, Guid nodeId,
        [FromBody] LoggingSettingsUpdateRequest request)
    {
        var userId = GetUserId();
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var node = await context.WorkflowNodes
            .FirstOrDefaultAsync(n => n.Id == nodeId && n.WorkflowId == workflowId &&
                                     context.Workflows.Any(w => w.Id == workflowId && w.OwnerId == userId));

        if (node == null) return NotFound(new { error = "Node not found." });

        node.LoggingSettingsJson = request.SettingsJson;
        await context.SaveChangesAsync();

        return Ok(new { message = "Logging settings updated.", nodeId, settings = request.SettingsJson });
    }
}

public class LoggingSettingsUpdateRequest
{
    public string SettingsJson { get; set; } = "";
}
