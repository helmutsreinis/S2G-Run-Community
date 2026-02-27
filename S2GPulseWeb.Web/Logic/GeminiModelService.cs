using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service to fetch and cache available models from the Gemini API
/// </summary>
public class GeminiModelService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UserSecretService _secretService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GeminiModelService> _logger;
    private const string CacheKey = "GeminiModels";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public GeminiModelService(
        IHttpClientFactory httpClientFactory, 
        UserSecretService secretService,
        IMemoryCache cache,
        ILogger<GeminiModelService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secretService = secretService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<GeminiModelInfo>> GetModelsAsync(string userId, string? apiKeyOverride = null)
    {
        // Try cache first
        if (_cache.TryGetValue(CacheKey, out List<GeminiModelInfo>? cachedModels) && cachedModels != null)
        {
            return cachedModels;
        }

        // Fetch from API
        var apiKey = !string.IsNullOrEmpty(apiKeyOverride) 
            ? apiKeyOverride 
            : await _secretService.GetSecretAsync(userId, "Gemini_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("No Gemini API key available for model fetching");
            return GetFallbackModels();
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var response = await httpClient.GetAsync($"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch Gemini models: {StatusCode}", response.StatusCode);
                return GetFallbackModels();
            }

            var content = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(content);
            
            var models = new List<GeminiModelInfo>();
            
            if (jsonResponse.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    var name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var displayName = model.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? name : name;
                    var description = model.TryGetProperty("description", out var d) ? d.GetString() : null;
                    
                    // Extract model ID from name (e.g., "models/gemini-2.0-flash" -> "gemini-2.0-flash")
                    var modelId = name.StartsWith("models/") ? name.Substring(7) : name;
                    
                    // Only include generative models (exclude embedding, vision-specific, etc.)
                    if (modelId.Contains("gemini") && !modelId.Contains("embedding") && 
                        !modelId.Contains("vision") && !modelId.Contains("aqa"))
                    {
                        models.Add(new GeminiModelInfo
                        {
                            Id = modelId,
                            DisplayName = displayName,
                            Description = description
                        });
                    }
                }
            }

            // Sort by display name
            models = models.OrderBy(m => m.DisplayName).ToList();

            // Cache the result
            _cache.Set(CacheKey, models, CacheDuration);
            
            _logger.LogInformation("Fetched {Count} models from Gemini API", models.Count);
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Gemini models");
            return GetFallbackModels();
        }
    }

    /// <summary>
    /// Fallback models when API is unavailable
    /// </summary>
    private static List<GeminiModelInfo> GetFallbackModels()
    {
        return new List<GeminiModelInfo>
        {
            // Gemini 2.5
            new() { Id = "gemini-2.5-pro", DisplayName = "Gemini 2.5 Pro" },
            new() { Id = "gemini-2.5-flash", DisplayName = "Gemini 2.5 Flash" },
            new() { Id = "gemini-2.5-flash-lite", DisplayName = "Gemini 2.5 Flash-Lite" },
            // Gemini 2.0
            new() { Id = "gemini-2.0-flash", DisplayName = "Gemini 2.0 Flash" },
            new() { Id = "gemini-2.0-flash-lite", DisplayName = "Gemini 2.0 Flash-Lite" },
            // Gemini 1.5
            new() { Id = "gemini-1.5-pro", DisplayName = "Gemini 1.5 Pro" },
            new() { Id = "gemini-1.5-flash", DisplayName = "Gemini 1.5 Flash" },
        };
    }

    public void InvalidateCache()
    {
        _cache.Remove(CacheKey);
    }
}

public class GeminiModelInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
}
