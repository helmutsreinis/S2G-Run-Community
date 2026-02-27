using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Scheduler node that allows users to define time-based scheduling configurations.
/// Supports interval, time of day, day of week, and day of month scheduling with timezone support.
/// When executed as part of a running workflow, it sets up a recurring timer.
/// </summary>
public class SchedulerNode : BaseNodeExecutor
{
    // Track active timers per node ID
    private static readonly ConcurrentDictionary<Guid, Timer> _activeTimers = new();
    
    // Track next run times for UI countdown display
    private static readonly ConcurrentDictionary<Guid, DateTime> _nextRunTimes = new();
    
    public SchedulerNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "Scheduler";

    public override List<string> GetOutputParameters() => new()
    {
        "SchedulerTriggeredAt", "SchedulerLocalTime", "SchedulerNextRun", 
        "SchedulerType", "SchedulerTimezone", "SchedulerExpired"
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<SchedulerConfig>(node.Configuration ?? "{}") ?? new();

        var now = DateTime.UtcNow;
        TimeZoneInfo timezone;
        
        try
        {
            timezone = TimeZoneInfo.FindSystemTimeZoneById(config.Timezone ?? "UTC");
        }
        catch
        {
            // Fallback to UTC if timezone not found
            timezone = TimeZoneInfo.Utc;
            Log(node, NodeLogLevel.Warning, "Invalid timezone", $"Timezone '{config.Timezone}' not found, using UTC");
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, timezone);
        
        // Check if schedule has expired (end date/time reached)
        bool hasExpired = false;
        if (!string.IsNullOrEmpty(config.EndDateTime) && 
            DateTime.TryParse(config.EndDateTime, out var endDateTime))
        {
            if (localNow >= endDateTime)
            {
                hasExpired = true;
                Log(node, NodeLogLevel.Warning, "Schedule expired", 
                    $"End date/time reached: {endDateTime:yyyy-MM-dd HH:mm}");
                
                // Stop any existing timer for this node
                StopTimer(node.Id);
                
                return new NodeExecutionResult
                {
                    Success = true,
                    OutputData = CreateOutputData(now, localNow, null, config, hasExpired)
                };
            }
        }
        
        // Calculate next run time based on schedule type
        DateTime? nextRun = CalculateNextRun(config, localNow, timezone);
        
        // For time-based schedules (not Interval), don't trigger immediately - just set up the timer
        bool shouldTriggerNow = config.ScheduleType == "Interval" || ShouldFireNow(config, localNow);
        
        if (shouldTriggerNow)
        {
            // Update last triggered timestamp
            config.LastTriggeredAt = now.ToString("yyyy-MM-dd HH:mm:ss");
            config.LastTriggeredLocalTime = localNow.ToString("yyyy-MM-dd HH:mm:ss");
            
            if (nextRun.HasValue)
            {
                config.NextScheduledRun = nextRun.Value.ToString("yyyy-MM-dd HH:mm:ss");
            }
            
            node.Configuration = JsonSerializer.Serialize(config);

            Log(node, NodeLogLevel.Info, "Schedule triggered", 
                $"Type: {config.ScheduleType}, Local time: {localNow:yyyy-MM-dd HH:mm:ss} ({config.Timezone})");
        }
        else
        {
            Log(node, NodeLogLevel.Info, "Scheduler waiting", 
                $"Type: {config.ScheduleType}, Next run: {nextRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "calculating..."} ({config.Timezone})");
        }

        // Set up recurring timer if we have an execution manager (running as part of workflow)
        if (_executionManager != null && !_activeTimers.ContainsKey(node.Id))
        {
            var intervalMs = CalculateIntervalMs(config);
            
            Log(node, NodeLogLevel.Debug, "Scheduler config", 
                $"Type: {config.ScheduleType}, Interval: {config.IntervalValue} {config.IntervalUnit}, Check interval: {intervalMs}ms");
            
            if (intervalMs > 0)
            {
                // Store next run time for UI countdown
                // For Interval: use the interval
                // For TimeOfDay/DayOfWeek/DayOfMonth: use the calculated next run time
                if (config.ScheduleType == "Interval")
                {
                    _nextRunTimes[node.Id] = DateTime.UtcNow.AddMilliseconds(intervalMs);
                }
                else if (nextRun.HasValue)
                {
                    // Convert calculated local next run to UTC for storage
                    _nextRunTimes[node.Id] = TimeZoneInfo.ConvertTimeToUtc(nextRun.Value, timezone);
                }
                
                Log(node, NodeLogLevel.Info, "Scheduler started", 
                    config.ScheduleType == "Interval" 
                        ? $"Next trigger in {TimeSpan.FromMilliseconds(intervalMs):hh\\:mm\\:ss}"
                        : $"Next trigger at {nextRun?.ToString("HH:mm:ss")} ({config.Timezone})");
                
                var timer = new Timer(
                    callback: _ => OnTimerTick(node.Id, config, timezone),
                    state: null,
                    dueTime: intervalMs,
                    period: intervalMs);
                
                _activeTimers[node.Id] = timer;
            }
        }

        var outputData = CreateOutputData(now, localNow, nextRun, config, hasExpired);

        await Task.CompletedTask;
        
        return new NodeExecutionResult
        {
            Success = shouldTriggerNow,  // Only mark as success (and trigger downstream) if it should fire now
            OutputData = outputData
        };
    }

    private void OnTimerTick(Guid nodeId, SchedulerConfig config, TimeZoneInfo timezone)
    {
        try
        {
            var now = DateTime.UtcNow;
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, timezone);
            
            // Check if expired
            if (!string.IsNullOrEmpty(config.EndDateTime) && 
                DateTime.TryParse(config.EndDateTime, out var endDateTime))
            {
                if (localNow >= endDateTime)
                {
                    _executionManager?.AddNodeLog(nodeId, NodeLogLevel.Warning, "Schedule expired", 
                        $"End date/time reached: {endDateTime:yyyy-MM-dd HH:mm}");
                    StopTimer(nodeId);
                    return;
                }
            }
            
            // Check if schedule should fire (for time-of-day, day-of-week, day-of-month)
            if (!ShouldFireNow(config, localNow))
            {
                // For time-based schedules, keep the calculated next run time (don't update every tick)
                // Only update for Interval type
                if (config.ScheduleType == "Interval")
                {
                    var intervalMs = CalculateIntervalMs(config);
                    if (intervalMs > 0)
                    {
                        _nextRunTimes[nodeId] = now.AddMilliseconds(intervalMs);
                    }
                }
                return;
            }
            
            var nextRun = CalculateNextRun(config, localNow, timezone);
            
            // Update next run time for UI countdown
            if (config.ScheduleType == "Interval")
            {
                var nextIntervalMs = CalculateIntervalMs(config);
                if (nextIntervalMs > 0)
                {
                    _nextRunTimes[nodeId] = now.AddMilliseconds(nextIntervalMs);
                }
            }
            else if (nextRun.HasValue)
            {
                // Convert calculated local next run to UTC for storage
                _nextRunTimes[nodeId] = TimeZoneInfo.ConvertTimeToUtc(nextRun.Value, timezone);
            }
            
            _executionManager?.AddNodeLog(nodeId, NodeLogLevel.Info, "Schedule triggered", 
                $"Local time: {localNow:yyyy-MM-dd HH:mm:ss}, Next: {nextRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
            
            var outputData = new Dictionary<string, object?>
            {
                { "SchedulerTriggeredAt", now.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                { "SchedulerLocalTime", localNow.ToString("yyyy-MM-dd HH:mm:ss") },
                { "SchedulerNextRun", nextRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "" },
                { "SchedulerType", config.ScheduleType ?? "Manual" },
                { "SchedulerTimezone", config.Timezone ?? "UTC" },
                { "SchedulerExpired", false }
            };
            
            // Trigger downstream nodes
            _executionManager?.TriggerNodeExecution(nodeId, outputData);
        }
        catch (Exception ex)
        {
            _executionManager?.AddNodeLog(nodeId, NodeLogLevel.Error, "Scheduler error", ex.Message);
        }
    }

    private bool ShouldFireNow(SchedulerConfig config, DateTime localNow)
    {
        // For Interval mode, always fire (timer handles the interval)
        if (config.ScheduleType == "Interval")
            return true;
        
        // For other modes, check if now matches the schedule (with 30-second tolerance)
        if (!TimeSpan.TryParse(config.TimeOfDay, out var targetTime))
            return false;
        
        var currentTime = localNow.TimeOfDay;
        var timeDiff = Math.Abs((currentTime - targetTime).TotalSeconds);
        
        // Within 30 seconds of target time
        if (timeDiff > 30)
            return false;
        
        // Check day of week if applicable
        if (config.ScheduleType == "DayOfWeek" && config.DaysOfWeek != null)
        {
            var todayName = localNow.DayOfWeek.ToString();
            if (!config.DaysOfWeek.Any(d => string.Equals(d, todayName, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        
        // Check day of month if applicable
        if (config.ScheduleType == "DayOfMonth" && config.DaysOfMonth != null)
        {
            if (!config.DaysOfMonth.Contains(localNow.Day))
                return false;
        }
        
        return true;
    }

    private long CalculateIntervalMs(SchedulerConfig config)
    {
        switch (config.ScheduleType)
        {
            case "Interval":
                return config.IntervalUnit switch
                {
                    "Seconds" => config.IntervalValue * 1000L,
                    "Minutes" => config.IntervalValue * 60 * 1000L,
                    "Hours" => config.IntervalValue * 60 * 60 * 1000L,
                    "Days" => config.IntervalValue * 24 * 60 * 60 * 1000L,
                    _ => config.IntervalValue * 60 * 1000L
                };
            
            case "TimeOfDay":
            case "DayOfWeek":
            case "DayOfMonth":
                // Check every minute for time-based schedules
                return 60 * 1000L;
            
            default:
                return 0;
        }
    }

    private Dictionary<string, object?> CreateOutputData(DateTime utcNow, DateTime localNow, DateTime? nextRun, SchedulerConfig config, bool hasExpired)
    {
        return new Dictionary<string, object?>
        {
            { "SchedulerTriggeredAt", utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            { "SchedulerLocalTime", localNow.ToString("yyyy-MM-dd HH:mm:ss") },
            { "SchedulerNextRun", nextRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "" },
            { "SchedulerType", config.ScheduleType ?? "Manual" },
            { "SchedulerTimezone", config.Timezone ?? "UTC" },
            { "SchedulerExpired", hasExpired }
        };
    }

    /// <summary>
    /// Stop the timer for a specific node (called when workflow stops)
    /// </summary>
    public static void StopTimer(Guid nodeId)
    {
        if (_activeTimers.TryRemove(nodeId, out var timer))
        {
            timer.Dispose();
        }
        _nextRunTimes.TryRemove(nodeId, out _);
    }

    /// <summary>
    /// Stop all scheduler timers (called on app shutdown)
    /// </summary>
    public static void StopAllTimers()
    {
        foreach (var kvp in _activeTimers)
        {
            kvp.Value.Dispose();
        }
        _activeTimers.Clear();
        _nextRunTimes.Clear();
    }

    /// <summary>
    /// Check if a scheduler node has an active timer
    /// </summary>
    public static bool IsActive(Guid nodeId) => _activeTimers.ContainsKey(nodeId);

    /// <summary>
    /// Get the countdown string for a scheduler node (e.g., "2:45" or "59s")
    /// Returns null if not active
    /// </summary>
    public static string? GetCountdown(Guid nodeId)
    {
        if (!_nextRunTimes.TryGetValue(nodeId, out var nextRun))
            return null;
        
        var remaining = nextRun - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0)
            return "0s";
        
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        if (remaining.TotalMinutes >= 1)
            return $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
        return $"{(int)remaining.TotalSeconds}s";
    }

    private DateTime? CalculateNextRun(SchedulerConfig config, DateTime localNow, TimeZoneInfo timezone)
    {
        try
        {
            switch (config.ScheduleType)
            {
                case "Interval":
                    var intervalMinutes = config.IntervalUnit switch
                    {
                        "Seconds" => config.IntervalValue / 60.0,
                        "Minutes" => config.IntervalValue,
                        "Hours" => config.IntervalValue * 60,
                        "Days" => config.IntervalValue * 60 * 24,
                        _ => config.IntervalValue
                    };
                    return localNow.AddMinutes(intervalMinutes);

                case "TimeOfDay":
                    if (TimeSpan.TryParse(config.TimeOfDay, out var time))
                    {
                        var nextRun = localNow.Date.Add(time);
                        if (nextRun <= localNow)
                            nextRun = nextRun.AddDays(1);
                        return nextRun;
                    }
                    break;

                case "DayOfWeek":
                    if (config.DaysOfWeek != null && config.DaysOfWeek.Any() && 
                        TimeSpan.TryParse(config.TimeOfDay, out var dowTime))
                    {
                        var targetDays = config.DaysOfWeek
                            .Select(d => Enum.TryParse<DayOfWeek>(d, true, out var dow) ? dow : (DayOfWeek?)null)
                            .Where(d => d.HasValue)
                            .Select(d => d!.Value)
                            .ToList();

                        if (targetDays.Any())
                        {
                            for (int i = 0; i <= 7; i++)
                            {
                                var checkDate = localNow.Date.AddDays(i);
                                if (targetDays.Contains(checkDate.DayOfWeek))
                                {
                                    var nextRun = checkDate.Add(dowTime);
                                    if (nextRun > localNow)
                                        return nextRun;
                                }
                            }
                        }
                    }
                    break;

                case "DayOfMonth":
                    if (config.DaysOfMonth != null && config.DaysOfMonth.Any() && 
                        TimeSpan.TryParse(config.TimeOfDay, out var domTime))
                    {
                        var currentMonth = new DateTime(localNow.Year, localNow.Month, 1);
                        
                        for (int m = 0; m < 2; m++) // Check this month and next
                        {
                            var checkMonth = currentMonth.AddMonths(m);
                            var daysInMonth = DateTime.DaysInMonth(checkMonth.Year, checkMonth.Month);
                            
                            foreach (var day in config.DaysOfMonth.OrderBy(d => d))
                            {
                                var actualDay = Math.Min(day, daysInMonth);
                                var nextRun = new DateTime(checkMonth.Year, checkMonth.Month, actualDay).Add(domTime);
                                if (nextRun > localNow)
                                    return nextRun;
                            }
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - next run calculation is informational
            System.Diagnostics.Debug.WriteLine($"Error calculating next run: {ex.Message}");
        }

        return null;
    }
}

public class SchedulerConfig
{
    public string? ScheduleType { get; set; } = "Interval"; // Interval, TimeOfDay, DayOfWeek, DayOfMonth
    
    // Interval settings
    public int IntervalValue { get; set; } = 1;  // Default to 1 minute
    public string? IntervalUnit { get; set; } = "Minutes"; // Seconds, Minutes, Hours, Days
    
    // Time of day settings (for TimeOfDay, DayOfWeek, DayOfMonth)
    public string? TimeOfDay { get; set; } = "09:00";
    
    // Day of week settings
    public List<string>? DaysOfWeek { get; set; }
    
    // Day of month settings
    public List<int>? DaysOfMonth { get; set; }
    
    // Timezone
    public string? Timezone { get; set; } = "UTC";
    
    // End date/time (optional - schedule stops after this)
    public string? EndDateTime { get; set; }
    
    // Statistics
    public string? LastTriggeredAt { get; set; }
    public string? LastTriggeredLocalTime { get; set; }
    public string? NextScheduledRun { get; set; }
}
