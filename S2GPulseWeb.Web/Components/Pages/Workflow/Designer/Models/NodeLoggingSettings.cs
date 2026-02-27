namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Settings for per-node log persistence configuration.
/// Logging is disabled by default to reduce database bloat.
/// </summary>
public class NodeLoggingSettings
{
    /// <summary>
    /// Master toggle for log persistence. When false, no logs are saved to database.
    /// </summary>
    public bool LoggingEnabled { get; set; } = false;

    /// <summary>Save Info level logs (general execution information)</summary>
    public bool LogInfo { get; set; } = true;

    /// <summary>Save Warning level logs (potential issues)</summary>
    public bool LogWarning { get; set; } = true;

    /// <summary>Save Error level logs (execution failures)</summary>
    public bool LogError { get; set; } = true;

    /// <summary>Save Debug level logs (detailed debugging data)</summary>
    public bool LogDebug { get; set; } = false;
}
