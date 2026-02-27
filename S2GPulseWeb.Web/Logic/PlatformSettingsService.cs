using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>A single card in the landing-page Capabilities grid.</summary>
public class CapabilityCard
{
    public string Icon { get; set; } = "⬡";
    public string Header { get; set; } = "";
    public string Description { get; set; } = "";
}

public static class DefaultCapabilityCards
{
    public static List<CapabilityCard> Get() => new()
    {
        new() { Icon = "⬡", Header = "AI Integration",    Description = "Connect OpenAI, DeepSeek, and custom AI models to power intelligent automation" },
        new() { Icon = "◈", Header = "HTTP Triggers",     Description = "Expose workflows as APIs with webhook triggers and HTTP listeners" },
        new() { Icon = "◎", Header = "Vector Storage",    Description = "Build RAG applications with built-in vector database for semantic search" },
        new() { Icon = "⟁", Header = "Visual Designer",   Description = "Drag-and-drop node editor with real-time execution visualization" },
        new() { Icon = "⬡", Header = "Cloud Connectors",  Description = "Integrate with Microsoft 365, OneDrive, and enterprise services" },
        new() { Icon = "⤢", Header = "Flow Control",      Description = "Conditional logic, loops, delays, and parallel execution paths" },
    };
}

/// <summary>
/// Cached branding values for the platform.
/// </summary>
public record PlatformBranding
{
    public string SiteName { get; init; } = "S2G";
    public string? FaviconSvg { get; init; }
    
    /// <summary>Landing page hero headline (e.g. "- Just Run It"). Null = use default.</summary>
    public string? LandingHeadline { get; init; }
    /// <summary>Landing page tagline (e.g. "Visual Workflow Automation Platform"). Null = use default.</summary>
    public string? LandingSubtitle { get; init; }
    /// <summary>Landing page description paragraph. Null = use default.</summary>
    public string? LandingDescription { get; init; }
    /// <summary>Capability cards shown on landing page Capabilities tab.</summary>
    public List<CapabilityCard> CapabilityCards { get; init; } = DefaultCapabilityCards.Get();
    
    /// <summary>
    /// Returns the favicon as a data URI for use in link[rel=icon], or null to fall back to default.
    /// </summary>
    public string? FaviconDataUri => FaviconSvg != null
        ? $"data:image/svg+xml,{Uri.EscapeDataString(FaviconSvg)}"
        : null;
}

/// <summary>
/// Singleton service for reading and writing platform branding settings.
/// Uses in-memory cache with DB persistence for container-safe deployment.
/// </summary>
public class PlatformSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private PlatformBranding? _cached;

    private const string KeySiteName = "SiteName";
    private const string KeyFaviconSvg = "FaviconSvg";
    private const string KeyLandingHeadline = "LandingHeadline";
    private const string KeyLandingSubtitle = "LandingSubtitle";
    private const string KeyLandingDescription = "LandingDescription";
    private const string KeyCapabilityCards = "CapabilityCards";

    public PlatformSettingsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Synchronous access to the cached site name. Returns default "S2G" if cache hasn't been loaded yet.
    /// The cache is guaranteed to be warm because App.razor calls EnsureLoaded() before child components render.
    /// </summary>
    public string SiteName => _cached?.SiteName ?? "S2G";

    /// <summary>
    /// Synchronous access to branding. Returns default branding if cache hasn't been loaded yet.
    /// </summary>
    public PlatformBranding Branding => _cached ?? new PlatformBranding();

    /// <summary>
    /// Ensures branding is loaded from DB. Call this once at startup (e.g. from App.razor).
    /// Blocks synchronously on first call, no-ops after that.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_cached != null) return;
        
        _lock.Wait();
        try
        {
            if (_cached != null) return;
            
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var settings = context.PlatformSettings.ToList();
            
            _cached = BuildBrandingFromSettings(settings);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get the current branding settings (cached after first load).
    /// </summary>
    public async Task<PlatformBranding> GetBrandingAsync()
    {
        if (_cached != null)
            return _cached;

        await _lock.WaitAsync();
        try
        {
            if (_cached != null)
                return _cached;

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var settings = await context.PlatformSettings.ToListAsync();
            
            _cached = BuildBrandingFromSettings(settings);

            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Save branding settings to DB and invalidate cache.
    /// </summary>
    public async Task SaveBrandingAsync(PlatformBranding branding)
    {
        await _lock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await UpsertSettingAsync(context, KeySiteName, branding.SiteName);
            await UpsertOrRemoveAsync(context, KeyFaviconSvg, branding.FaviconSvg);
            await UpsertOrRemoveAsync(context, KeyLandingHeadline, branding.LandingHeadline);
            await UpsertOrRemoveAsync(context, KeyLandingSubtitle, branding.LandingSubtitle);
            await UpsertOrRemoveAsync(context, KeyLandingDescription, branding.LandingDescription);
            // Persist capability cards as JSON; remove row when matching defaults to keep DB clean
            var cardsJson = JsonSerializer.Serialize(branding.CapabilityCards);
            await UpsertSettingAsync(context, KeyCapabilityCards, cardsJson);

            await context.SaveChangesAsync();
            _cached = branding;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Clear the favicon SVG from the database.
    /// </summary>
    public async Task ClearFaviconAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var setting = await context.PlatformSettings.FirstOrDefaultAsync(s => s.Key == KeyFaviconSvg);
            if (setting != null)
            {
                context.PlatformSettings.Remove(setting);
                await context.SaveChangesAsync();
            }

            _cached = _cached != null
                ? _cached with { FaviconSvg = null }
                : new PlatformBranding();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reset all branding to defaults (removes DB entries, clears cache).
    /// </summary>
    public async Task ResetToDefaultsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var allKeys = new[] { KeySiteName, KeyFaviconSvg, KeyLandingHeadline, KeyLandingSubtitle, KeyLandingDescription, KeyCapabilityCards };
            var settings = await context.PlatformSettings
                .Where(s => allKeys.Contains(s.Key))
                .ToListAsync();
                
            context.PlatformSettings.RemoveRange(settings);
            await context.SaveChangesAsync();

            _cached = new PlatformBranding();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static PlatformBranding BuildBrandingFromSettings(List<PlatformSetting> settings)
    {
        string? Val(string key) => settings.FirstOrDefault(s => s.Key == key)?.Value;

        List<CapabilityCard> cards;
        var cardsRaw = Val(KeyCapabilityCards);
        try { cards = cardsRaw != null ? JsonSerializer.Deserialize<List<CapabilityCard>>(cardsRaw) ?? DefaultCapabilityCards.Get() : DefaultCapabilityCards.Get(); }
        catch { cards = DefaultCapabilityCards.Get(); }

        return new PlatformBranding
        {
            SiteName = Val(KeySiteName) ?? "S2G",
            FaviconSvg = Val(KeyFaviconSvg),
            LandingHeadline = Val(KeyLandingHeadline),
            LandingSubtitle = Val(KeyLandingSubtitle),
            LandingDescription = Val(KeyLandingDescription),
            CapabilityCards = cards
        };
    }

    private static async Task UpsertSettingAsync(ApplicationDbContext context, string key, string value)
    {
        var existing = await context.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (existing != null)
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            context.PlatformSettings.Add(new PlatformSetting
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow
            });
        }
    }

    private static async Task UpsertOrRemoveAsync(ApplicationDbContext context, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            await UpsertSettingAsync(context, key, value);
        }
        else
        {
            var existing = await context.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (existing != null)
                context.PlatformSettings.Remove(existing);
        }
    }
}
