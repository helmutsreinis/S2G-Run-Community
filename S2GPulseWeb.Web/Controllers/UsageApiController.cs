using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// REST API for checking quota/usage information.
/// </summary>
[ApiController]
[Route("api/v1/usage")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class UsageApiController : ControllerBase
{
    private readonly UsageTrackingService _usageTrackingService;

    public UsageApiController(UsageTrackingService usageTrackingService)
    {
        _usageTrackingService = usageTrackingService;
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>Get full usage breakdown: executions, storage, vector docs, and workflows.</summary>
    [HttpGet]
    public async Task<IActionResult> GetUsage()
    {
        var userId = GetUserId();
        var percentages = await _usageTrackingService.GetUsagePercentagesAsync(userId);

        return Ok(new
        {
            executions = new
            {
                used = percentages.ExecutionsUsed,
                limit = percentages.ExecutionsLimit,
                percent = Math.Round(percentages.ExecutionsPercent, 1)
            },
            storage = new
            {
                usedBytes = percentages.StorageUsed,
                limitBytes = percentages.StorageLimit,
                percent = Math.Round(percentages.StoragePercent, 1)
            },
            vectorDocs = new
            {
                used = percentages.VectorDocsUsed,
                limit = percentages.VectorDocsLimit,
                percent = Math.Round(percentages.VectorDocsPercent, 1)
            },
            workflows = new
            {
                used = percentages.WorkflowsUsed,
                limit = percentages.WorkflowsLimit,
                percent = Math.Round(percentages.WorkflowsPercent, 1)
            }
        });
    }
}
