using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service to fetch and cache available models from the Anthropic API
/// </summary>
public class AnthropicModelService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UserSecretService _secretService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AnthropicModelService> _logger;
    private const string CacheKey = "AnthropicModels";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public AnthropicModelService(
        IHttpClientFactory httpClientFactory, 
        UserSecretService secretService,
        IMemoryCache cache,
        ILogger<AnthropicModelService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secretService = secretService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<AnthropicModelInfo>> GetModelsAsync(string userId, string? apiKeyOverride = null)
    {
        // Try cache first
        if (_cache.TryGetValue(CacheKey, out List<AnthropicModelInfo>? cachedModels) && cachedModels != null)
        {
            return cachedModels;
        }

        // Fetch from API
        var apiKey = !string.IsNullOrEmpty(apiKeyOverride) 
            ? apiKeyOverride 
            : await _secretService.GetSecretAsync(userId, "Anthropic_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("No Anthropic API key available for model fetching");
            return GetFallbackModels();
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            var response = await httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch Anthropic models: {StatusCode}", response.StatusCode);
                return GetFallbackModels();
            }

            var content = await response.Content.ReadAsStringAsync();
            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(content);
            
            var models = new List<AnthropicModelInfo>();
            
            if (jsonResponse.TryGetProperty("data", out var dataArray))
            {
                foreach (var model in dataArray.EnumerateArray())
                {
                    var id = model.GetProperty("id").GetString() ?? "";
                    var displayName = model.TryGetProperty("display_name", out var dn) 
                        ? dn.GetString() ?? id 
                        : id;
                    var createdAt = model.TryGetProperty("created_at", out var ca) 
                        ? ca.GetString() 
                        : null;
                    
                    models.Add(new AnthropicModelInfo
                    {
                        Id = id,
                        DisplayName = displayName,
                        CreatedAt = createdAt
                    });
                }
            }

            // Sort by display name
            models = models.OrderBy(m => m.DisplayName).ToList();

            // Cache the result
            _cache.Set(CacheKey, models, CacheDuration);
            
            _logger.LogInformation("Fetched {Count} models from Anthropic API", models.Count);
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Anthropic models");
            return GetFallbackModels();
        }
    }

    /// <summary>
    /// Fallback models when API is unavailable
    /// </summary>
    private static List<AnthropicModelInfo> GetFallbackModels()
    {
        return new List<AnthropicModelInfo>
        {
            // Claude 4.5
            new() { Id = "claude-opus-4-5-20250514", DisplayName = "Claude Opus 4.5" },
            new() { Id = "claude-sonnet-4-5-20250514", DisplayName = "Claude Sonnet 4.5" },
            new() { Id = "claude-haiku-4-5-20250514", DisplayName = "Claude Haiku 4.5" },
            // Claude 4
            new() { Id = "claude-opus-4-20250514", DisplayName = "Claude Opus 4.1" },
            new() { Id = "claude-sonnet-4-20250514", DisplayName = "Claude Sonnet 4" },
            // Claude 3.7
            new() { Id = "claude-3-7-sonnet-20250219", DisplayName = "Claude 3.7 Sonnet" },
            // Claude 3.5
            new() { Id = "claude-3-5-sonnet-20241022", DisplayName = "Claude 3.5 Sonnet" },
            new() { Id = "claude-3-5-haiku-20241022", DisplayName = "Claude 3.5 Haiku" },
            // Claude 3
            new() { Id = "claude-3-opus-20240229", DisplayName = "Claude 3 Opus" },
            new() { Id = "claude-3-haiku-20240307", DisplayName = "Claude 3 Haiku" },
        };
    }

    public void InvalidateCache()
    {
        _cache.Remove(CacheKey);
    }
}

public class AnthropicModelInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? CreatedAt { get; set; }
}
