namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Key-value store for platform-wide settings (white-label branding, etc.)
/// </summary>
public class PlatformSetting
{
    public int Id { get; set; }
    
    /// <summary>
    /// Unique setting key, e.g. "SiteName", "FaviconSvg"
    /// </summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// Setting value (text content, SVG markup, etc.)
    /// </summary>
    public string Value { get; set; } = string.Empty;
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
