using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace S2GPulseWeb.Web.Logic;

public class GroqModelService
{
    private readonly IMemoryCache _cache;
    private const string CacheKey = "GroqModels";

    public GroqModelService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task<List<GroqModelInfo>> GetModelsAsync(string userId, string? apiKeyOverride = null)
    {
        // Return curated list of models with predictive pricing only
        // We don't fetch from API because we want consistent, documented pricing
        return Task.FromResult(GetCuratedModels());
    }

    public void InvalidateCache()
    {
        _cache.Remove(CacheKey);
    }

    private static List<GroqModelInfo> GetCuratedModels()
    {
        return new List<GroqModelInfo>
        {
            // GPT OSS Models (Fast inference)
            new() { Id = "openai/gpt-oss-20b", DisplayName = "GPT OSS 20B (128k)", Category = "GPT OSS" },
            new() { Id = "openai/gpt-oss-safeguard-20b", DisplayName = "GPT OSS Safeguard 20B", Category = "GPT OSS" },
            new() { Id = "openai/gpt-oss-120b", DisplayName = "GPT OSS 120B (128k)", Category = "GPT OSS" },
            
            // Partner Models
            new() { Id = "moonshotai/kimi-k2-instruct-0905", DisplayName = "Kimi K2-0905 (256k)", Category = "Partner" },
            
            // Llama 4 Models
            new() { Id = "llama-4-scout-17b-16e", DisplayName = "Llama 4 Scout (17Bx16E, 128k)", Category = "Llama 4" },
            new() { Id = "llama-4-maverick-17b-128e", DisplayName = "Llama 4 Maverick (17Bx128E, 128k)", Category = "Llama 4" },
            new() { Id = "llama-guard-4-12b", DisplayName = "Llama Guard 4 12B (128k)", Category = "Llama 4" },
            
            // Open Models
            new() { Id = "qwen-3-32b", DisplayName = "Qwen3 32B (131k)", Category = "Open" },
            new() { Id = "llama-3.3-70b-versatile", DisplayName = "Llama 3.3 70B Versatile (128k)", Category = "Open" },
            new() { Id = "llama-3.1-8b-instant", DisplayName = "Llama 3.1 8B Instant (128k)", Category = "Open" },
        };
    }
}

public class GroqModelInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "Open";
}
