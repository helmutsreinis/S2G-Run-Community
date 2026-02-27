using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Manages API key lifecycle: creation, validation, revocation, and listing.
/// Keys use the format pls_{32-char-random} and are stored as SHA-256 hashes.
/// </summary>
public class ApiKeyService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<ApiKeyService> _logger;

    public ApiKeyService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<ApiKeyService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new API key. Returns the plain-text key (shown only once).
    /// </summary>
    public async Task<(ApiKey Key, string PlainTextKey)> CreateKeyAsync(
        string userId, string name, DateTime? expiresAt = null)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(24);
        var randomPart = Convert.ToBase64String(rawBytes)
            .Replace("+", "").Replace("/", "").Replace("=", "")[..32];
        var plainTextKey = $"pls_{randomPart}";

        var hash = HashKey(plainTextKey);

        var apiKey = new ApiKey
        {
            UserId = userId,
            Name = name,
            KeyHash = hash,
            KeyPrefix = plainTextKey[..12], // "pls_" + 8 chars
            ExpiresAt = expiresAt
        };

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created API key '{Name}' for user {UserId}", name, userId);
        return (apiKey, plainTextKey);
    }

    /// <summary>
    /// Validates a raw API key. Returns the userId if valid, null otherwise.
    /// </summary>
    public async Task<string?> ValidateKeyAsync(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey) || !rawKey.StartsWith("pls_"))
            return null;

        var hash = HashKey(rawKey);

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var apiKey = await context.ApiKeys
            .FirstOrDefaultAsync(k => k.KeyHash == hash);

        if (apiKey == null) return null;
        if (apiKey.IsRevoked) return null;
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow) return null;

        // Update last used timestamp
        apiKey.LastUsedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        return apiKey.UserId;
    }

    /// <summary>
    /// Revokes a key (soft delete — keeps the record but marks it inactive).
    /// </summary>
    public async Task<bool> RevokeKeyAsync(Guid keyId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var key = await context.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);

        if (key == null) return false;

        key.IsRevoked = true;
        await context.SaveChangesAsync();

        _logger.LogInformation("Revoked API key {KeyId} for user {UserId}", keyId, userId);
        return true;
    }

    /// <summary>
    /// Lists all keys for a user (no sensitive data exposed).
    /// </summary>
    public async Task<List<ApiKey>> GetUserKeysAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Permanently deletes a key.
    /// </summary>
    public async Task<bool> DeleteKeyAsync(Guid keyId, string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var key = await context.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);

        if (key == null) return false;

        context.ApiKeys.Remove(key);
        await context.SaveChangesAsync();

        _logger.LogInformation("Deleted API key {KeyId} for user {UserId}", keyId, userId);
        return true;
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
