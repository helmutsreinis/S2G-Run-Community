using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Background service that periodically cleans up disabled organizations
/// whose 30-day grace period has expired.
/// </summary>
public class OrganizationCleanupBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OrganizationCleanupBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);
    private const int GracePeriodDays = 30;

    public OrganizationCleanupBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<OrganizationCleanupBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Organization Cleanup Background Service started. Interval: {Interval}, Grace Period: {GracePeriod} days",
            _interval, GracePeriodDays);

        // Initial delay to let the application start up
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Starting organization cleanup cycle...");
                await CleanupExpiredOrganizationsAsync();
                _logger.LogDebug("Organization cleanup cycle completed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during organization cleanup.");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("Organization Cleanup Background Service stopped.");
    }

    private async Task CleanupExpiredOrganizationsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var organizationService = scope.ServiceProvider.GetRequiredService<OrganizationService>();

        await using var db = await dbContextFactory.CreateDbContextAsync();

        var cutoffDate = DateTime.UtcNow.AddDays(-GracePeriodDays);

        var expiredOrgs = await db.Organizations
            .Where(o => o.IsActive && o.IsDisabled && o.DisabledAt != null && o.DisabledAt <= cutoffDate)
            .Select(o => new { o.Id, o.Name, o.FounderId, o.DisabledAt })
            .ToListAsync();

        if (!expiredOrgs.Any())
        {
            _logger.LogDebug("No expired disabled organizations found.");
            return;
        }

        _logger.LogInformation("Found {Count} disabled organization(s) past the {Days}-day grace period.", 
            expiredOrgs.Count, GracePeriodDays);

        foreach (var org in expiredOrgs)
        {
            try
            {
                _logger.LogInformation(
                    "Deleting expired organization {OrgId} ({OrgName}), disabled since {DisabledAt:u}",
                    org.Id, org.Name, org.DisabledAt);

                // Try transfer-to-founder first, fall back to delete-all
                var result = await organizationService.AdminDeleteOrganizationAsync(org.Id);

                if (result.Success)
                {
                    _logger.LogInformation(
                        "Successfully deleted organization {OrgId} ({OrgName}). Workflows transferred: {Transferred}, deleted: {Deleted}",
                        org.Id, org.Name, result.WorkflowsTransferred, result.WorkflowsDeleted);
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to delete organization {OrgId} ({OrgName}): {Error}",
                        org.Id, org.Name, result.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting expired organization {OrgId} ({OrgName})", org.Id, org.Name);
            }
        }
    }
}
