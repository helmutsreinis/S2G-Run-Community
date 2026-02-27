using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing admin-defined platform connectors and categories.
/// Handles CRUD operations and secret encryption.
/// </summary>
public class PlatformConnectorService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<PlatformConnectorService> _logger;
    
    private const string ProtectorPurpose = "PlatformConnectorSecrets";

    public PlatformConnectorService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IDataProtectionProvider dataProtection,
        ILogger<PlatformConnectorService> logger)
    {
        _dbFactory = dbFactory;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    #region Categories

    /// <summary>Gets all categories ordered by DisplayOrder.</summary>
    public async Task<List<ConnectorCategory>> GetCategoriesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ConnectorCategories
            .OrderBy(c => c.DisplayOrder)
            .ToListAsync();
    }

    /// <summary>Gets a category by ID with its connectors.</summary>
    public async Task<ConnectorCategory?> GetCategoryAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ConnectorCategories
            .Include(c => c.Connectors)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Creates a new category.</summary>
    public async Task<ConnectorCategory> CreateCategoryAsync(string name, string? description, string iconEmoji)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        
        var maxOrder = await db.ConnectorCategories.MaxAsync(c => (int?)c.DisplayOrder) ?? 0;
        
        var category = new ConnectorCategory
        {
            Name = name,
            Description = description,
            IconEmoji = iconEmoji,
            DisplayOrder = maxOrder + 1
        };

        db.ConnectorCategories.Add(category);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Created connector category: {Name}", name);
        return category;
    }

    /// <summary>Updates an existing category.</summary>
    public async Task<bool> UpdateCategoryAsync(Guid id, string name, string? description, string iconEmoji, int displayOrder)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var category = await db.ConnectorCategories.FindAsync(id);
        if (category == null) return false;

        category.Name = name;
        category.Description = description;
        category.IconEmoji = iconEmoji;
        category.DisplayOrder = displayOrder;
        
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Deletes a category. Connectors in this category become uncategorized.</summary>
    public async Task<bool> DeleteCategoryAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var category = await db.ConnectorCategories.FindAsync(id);
        if (category == null) return false;

        db.ConnectorCategories.Remove(category);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Deleted connector category: {Name}", category.Name);
        return true;
    }

    #endregion

    #region Connectors

    /// <summary>Gets all connectors with their categories.</summary>
    public async Task<List<PlatformConnector>> GetConnectorsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.PlatformConnectors
            .Include(c => c.Category)
            .OrderBy(c => c.Category != null ? c.Category.DisplayOrder : int.MaxValue)
            .ThenBy(c => c.DisplayOrder)
            .ToListAsync();
    }

    /// <summary>Gets only enabled connectors grouped by category for user-facing display.</summary>
    public async Task<List<PlatformConnector>> GetEnabledConnectorsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.PlatformConnectors
            .Include(c => c.Category)
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.Category != null ? c.Category.DisplayOrder : int.MaxValue)
            .ThenBy(c => c.DisplayOrder)
            .ToListAsync();
    }

    /// <summary>Gets a connector by ID.</summary>
    public async Task<PlatformConnector?> GetConnectorAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.PlatformConnectors
            .Include(c => c.Category)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Creates a new connector with plain text secret.</summary>
    public async Task<PlatformConnector> CreateConnectorAsync(
        Guid? categoryId,
        string name,
        string description,
        ConnectorConsentType consentType,
        string clientId,
        string clientSecret,
        string tenantId,
        string requiredScopes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        
        var maxOrder = categoryId.HasValue
            ? await db.PlatformConnectors.Where(c => c.CategoryId == categoryId).MaxAsync(c => (int?)c.DisplayOrder) ?? 0
            : await db.PlatformConnectors.Where(c => c.CategoryId == null).MaxAsync(c => (int?)c.DisplayOrder) ?? 0;

        var connector = new PlatformConnector
        {
            CategoryId = categoryId,
            Name = name,
            Description = description,
            ConsentType = consentType,
            ClientId = clientId,
            ClientSecret = clientSecret,  // Store as plain text
            TenantId = tenantId,
            RequiredScopes = requiredScopes,
            DisplayOrder = maxOrder + 1
        };

        db.PlatformConnectors.Add(connector);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Created platform connector: {Name} ({ConsentType})", name, consentType);
        return connector;
    }

    /// <summary>Updates an existing connector. Pass empty string for secret to keep existing.</summary>
    public async Task<bool> UpdateConnectorAsync(
        Guid id,
        Guid? categoryId,
        string name,
        string description,
        ConnectorConsentType consentType,
        string clientId,
        string? clientSecret,
        string tenantId,
        string requiredScopes,
        bool isEnabled,
        int displayOrder)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connector = await db.PlatformConnectors.FindAsync(id);
        if (connector == null) return false;

        connector.CategoryId = categoryId;
        connector.Name = name;
        connector.Description = description;
        connector.ConsentType = consentType;
        connector.ClientId = clientId;
        connector.TenantId = tenantId;
        connector.RequiredScopes = requiredScopes;
        connector.IsEnabled = isEnabled;
        connector.DisplayOrder = displayOrder;
        connector.UpdatedAt = DateTime.UtcNow;
        
        // Only update secret if provided
        if (!string.IsNullOrEmpty(clientSecret))
        {
            connector.ClientSecret = clientSecret;  // Store as plain text
        }
        
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Deletes a connector. User connections to this connector are preserved.</summary>
    public async Task<bool> DeleteConnectorAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connector = await db.PlatformConnectors.FindAsync(id);
        if (connector == null) return false;

        db.PlatformConnectors.Remove(connector);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("Deleted platform connector: {Name}", connector.Name);
        return true;
    }

    /// <summary>Gets client secret for OAuth flow (plain text).</summary>
    public async Task<string?> GetClientSecretAsync(Guid connectorId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connector = await db.PlatformConnectors.FindAsync(connectorId);
        if (connector == null) return null;

        // Return plain text secret, fallback to decrypt legacy encrypted secret
        if (!string.IsNullOrEmpty(connector.ClientSecret))
            return connector.ClientSecret;
        
        // Migration fallback: decrypt old encrypted secret if exists
        if (!string.IsNullOrEmpty(connector.ClientSecretEncrypted))
            return DecryptSecret(connector.ClientSecretEncrypted);
        
        return null;
    }

    #endregion

    #region Encryption

    private string EncryptSecret(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        return _protector.Protect(plainText);
    }

    private string DecryptSecret(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText)) return "";
        try
        {
            return _protector.Unprotect(encryptedText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt connector secret");
            return "";
        }
    }

    #endregion
}
