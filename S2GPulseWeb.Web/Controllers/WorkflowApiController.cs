using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// REST API for workflow CRUD, start, and stop operations.
/// </summary>
[ApiController]
[Route("api/v1/workflows")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class WorkflowApiController : ControllerBase
{
    private readonly WorkflowApiService _workflowApiService;

    public WorkflowApiController(WorkflowApiService workflowApiService)
    {
        _workflowApiService = workflowApiService;
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>List all workflows for the authenticated user's active context.</summary>
    [HttpGet]
    public async Task<IActionResult> ListWorkflows()
    {
        var userId = GetUserId();
        var workflows = await _workflowApiService.ListWorkflowsAsync(userId);
        return Ok(workflows);
    }

    /// <summary>Get a specific workflow with full node and connection details.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetWorkflow(Guid id)
    {
        var result = await _workflowApiService.GetWorkflowAsync(GetUserId(), id);
        if (result == null) return NotFound(new { error = "Workflow not found." });
        return Ok(result);
    }

    /// <summary>Create a new workflow with auto-layout and auto-labeling.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateWorkflow([FromBody] WorkflowCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Workflow name is required." });

        var (result, invalidTypes) = await _workflowApiService.CreateWorkflowAsync(GetUserId(), request);
        if (invalidTypes != null)
            return BadRequest(new { error = $"Unknown node type(s): {string.Join(", ", invalidTypes)}. Use GET /api/v1/catalog/nodes to see available types." });
        return CreatedAtAction(nameof(GetWorkflow), new { id = result!.Id }, result);
    }

    /// <summary>Update an existing workflow.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateWorkflow(Guid id, [FromBody] WorkflowUpdateRequest request)
    {
        var (result, invalidTypes) = await _workflowApiService.UpdateWorkflowAsync(GetUserId(), id, request);
        if (invalidTypes != null)
            return BadRequest(new { error = $"Unknown node type(s): {string.Join(", ", invalidTypes)}. Use GET /api/v1/catalog/nodes to see available types." });
        if (result == null) return NotFound(new { error = "Workflow not found." });
        return Ok(result);
    }

    /// <summary>Delete a workflow and all associated data.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteWorkflow(Guid id)
    {
        var result = await _workflowApiService.DeleteWorkflowAsync(GetUserId(), id);
        if (!result.Success) return NotFound(new { error = result.ErrorMessage });
        return Ok(result);
    }

    /// <summary>Start a workflow (sets IsActive=true for auto-restart durability).</summary>
    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> StartWorkflow(Guid id)
    {
        var (success, error) = await _workflowApiService.StartWorkflowAsync(GetUserId(), id);
        if (!success) return BadRequest(new { error });
        return Ok(new { message = "Workflow started.", isActive = true });
    }

    /// <summary>Stop a workflow (sets IsActive=false).</summary>
    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> StopWorkflow(Guid id)
    {
        var (success, error) = await _workflowApiService.StopWorkflowAsync(GetUserId(), id);
        if (!success) return BadRequest(new { error });
        return Ok(new { message = "Workflow stopped.", isActive = false });
    }

    // ── Node Sub-Resources ──────────────────────────────────────────

    /// <summary>Add a single node to an existing workflow.</summary>
    [HttpPost("{id:guid}/nodes")]
    public async Task<IActionResult> AddNode(Guid id, [FromBody] AddNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NodeType))
            return BadRequest(new { error = "NodeType is required." });
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        var (result, invalidTypes) = await _workflowApiService.AddNodeAsync(GetUserId(), id, request);
        if (invalidTypes != null)
            return BadRequest(new { error = $"Unknown node type: {string.Join(", ", invalidTypes)}. Use GET /api/v1/catalog/nodes to see available types." });
        if (result == null) return NotFound(new { error = "Workflow not found." });
        return CreatedAtAction(nameof(GetWorkflow), new { id }, result);
    }

    /// <summary>Remove a node (and its connections) from a workflow.</summary>
    [HttpDelete("{id:guid}/nodes/{nodeId:guid}")]
    public async Task<IActionResult> RemoveNode(Guid id, Guid nodeId)
    {
        var result = await _workflowApiService.RemoveNodeAsync(GetUserId(), id, nodeId);
        if (!result) return NotFound(new { error = "Workflow or node not found." });
        return Ok(new { message = "Node deleted." });
    }

    // ── Connection Sub-Resources ────────────────────────────────────

    /// <summary>Add a connection between two existing nodes in a workflow.</summary>
    [HttpPost("{id:guid}/connections")]
    public async Task<IActionResult> AddConnection(Guid id, [FromBody] AddConnectionRequest request)
    {
        if (request.SourceNodeId == Guid.Empty || request.TargetNodeId == Guid.Empty)
            return BadRequest(new { error = "SourceNodeId and TargetNodeId are required." });

        var result = await _workflowApiService.AddConnectionAsync(GetUserId(), id, request);
        if (result == null) return NotFound(new { error = "Workflow or nodes not found." });
        return CreatedAtAction(nameof(GetWorkflow), new { id }, result);
    }

    /// <summary>Remove a connection from a workflow.</summary>
    [HttpDelete("{id:guid}/connections/{connectionId:guid}")]
    public async Task<IActionResult> RemoveConnection(Guid id, Guid connectionId)
    {
        var result = await _workflowApiService.RemoveConnectionAsync(GetUserId(), id, connectionId);
        if (!result) return NotFound(new { error = "Workflow or connection not found." });
        return Ok(new { message = "Connection deleted." });
    }
}
