using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

public class MistralModelService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UserSecretService _secretService;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "MistralModels";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public MistralModelService(IHttpClientFactory httpClientFactory, UserSecretService secretService, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _secretService = secretService;
        _cache = cache;
    }

    public Task<List<MistralModelInfo>> GetModelsAsync(string userId, string? apiKeyOverride = null)
    {
        // Return curated list of Premier + General purpose models only
        // We don't fetch from API because it returns too many models (versioned, experimental, etc.)
        return Task.FromResult(GetFallbackModels());
    }

    public void InvalidateCache()
    {
        _cache.Remove(CacheKey);
    }

    private static List<MistralModelInfo> GetFallbackModels()
    {
        return new List<MistralModelInfo>
        {
            // Premier Models (State-of-the-Art)
            new() { Id = "mistral-large-latest", DisplayName = "Mistral Large 2", Category = "Premier" },
            new() { Id = "pixtral-large-latest", DisplayName = "Pixtral Large", Category = "Premier" },
            new() { Id = "mistral-medium-latest", DisplayName = "Mistral Medium (Legacy)", Category = "Premier" },
            new() { Id = "codestral-latest", DisplayName = "Codestral", Category = "Premier" },
            new() { Id = "codestral-mamba-latest", DisplayName = "Codestral Mamba", Category = "Premier" },
            
            // General Purpose (Open Source / Edge)
            new() { Id = "mistral-small-latest", DisplayName = "Mistral Small", Category = "General" },
            new() { Id = "ministral-8b-latest", DisplayName = "Ministral 8B", Category = "General" },
            new() { Id = "ministral-3b-latest", DisplayName = "Ministral 3B", Category = "General" },
            new() { Id = "open-mistral-nemo", DisplayName = "Mistral Nemo", Category = "General" },
            new() { Id = "open-mixtral-8x22b", DisplayName = "Mixtral 8x22B", Category = "General" },
            new() { Id = "open-mixtral-8x7b", DisplayName = "Mixtral 8x7B", Category = "General" },
            new() { Id = "open-mistral-7b", DisplayName = "Mistral 7B", Category = "General" },
        };
    }

    private static string FormatDisplayName(string modelId)
    {
        return modelId switch
        {
            "mistral-large-latest" => "Mistral Large 2",
            "pixtral-large-latest" => "Pixtral Large",
            "mistral-medium-latest" => "Mistral Medium (Legacy)",
            "codestral-latest" => "Codestral",
            "codestral-mamba-latest" => "Codestral Mamba",
            "mistral-small-latest" => "Mistral Small",
            "ministral-8b-latest" => "Ministral 8B",
            "ministral-3b-latest" => "Ministral 3B",
            "open-mistral-nemo" => "Mistral Nemo",
            "open-mixtral-8x22b" => "Mixtral 8x22B",
            "open-mixtral-8x7b" => "Mixtral 8x7B",
            "open-mistral-7b" => "Mistral 7B",
            _ => modelId
        };
    }

    private static string GetModelCategory(string modelId)
    {
        // Premier models
        if (modelId.Contains("large") || modelId.Contains("medium") || modelId.StartsWith("codestral") || modelId.StartsWith("pixtral"))
            return "Premier";
        
        // General purpose
        return "General";
    }
}

public class MistralModelInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "General";
}
