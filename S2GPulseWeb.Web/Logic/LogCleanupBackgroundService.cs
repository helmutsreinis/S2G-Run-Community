using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Background service that periodically cleans up expired logs based on retention settings
/// </summary>
public class LogCleanupBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LogCleanupBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5); // Run every 5 minutes

    public LogCleanupBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<LogCleanupBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Log Cleanup Background Service started. Interval: {Interval}", _interval);

        // Initial delay to let the application start up
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Starting log cleanup cycle...");
                await CleanupExpiredLogsAsync();
                _logger.LogDebug("Log cleanup cycle completed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during log cleanup.");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("Log Cleanup Background Service stopped.");
    }

    private async Task CleanupExpiredLogsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var logService = scope.ServiceProvider.GetRequiredService<NodeLogService>();

        var retentionSettings = await logService.GetAllRetentionSettingsAsync();

        if (!retentionSettings.Any())
        {
            _logger.LogDebug("No retention settings found. Users need to configure retention in Log Viewer.");
            return;
        }

        _logger.LogDebug("Processing {Count} retention setting(s)", retentionSettings.Count);

        foreach (var setting in retentionSettings)
        {
            try
            {
                var cutoffDate = NodeLogService.CalculateCutoffDate(setting);
                _logger.LogDebug(
                    "User {UserId}: Retention {Value} {Unit}, deleting logs before {CutoffDate:u}",
                    setting.UserId, setting.RetentionValue, setting.RetentionUnit, cutoffDate);
                
                var deletedCount = await logService.DeleteLogsOlderThanAsync(setting.UserId, cutoffDate);

                if (deletedCount > 0)
                {
                    _logger.LogInformation(
                        "Deleted {Count} expired logs for user {UserId} (retention: {Value} {Unit})",
                        deletedCount, setting.UserId, setting.RetentionValue, setting.RetentionUnit);
                }
                else
                {
                    _logger.LogDebug("No expired logs found for user {UserId}", setting.UserId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up logs for user {UserId}", setting.UserId);
            }
        }
    }
}
