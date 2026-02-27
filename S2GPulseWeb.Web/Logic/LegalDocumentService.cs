using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing versioned legal documents (Terms of Service, Privacy Policy)
/// </summary>
public class LegalDocumentService
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

    public LegalDocumentService(IDbContextFactory<ApplicationDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Get the currently active document for a given type
    /// </summary>
    public async Task<LegalDocument?> GetActiveDocumentAsync(LegalDocumentType type)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.LegalDocuments
            .Where(d => d.Type == type && d.IsActive)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Get all versions of a document type for admin management
    /// </summary>
    public async Task<List<LegalDocument>> GetAllVersionsAsync(LegalDocumentType type)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.LegalDocuments
            .Where(d => d.Type == type)
            .OrderByDescending(d => d.Version)
            .ToListAsync();
    }

    /// <summary>
    /// Get a specific document by ID
    /// </summary>
    public async Task<LegalDocument?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.LegalDocuments.FindAsync(id);
    }

    /// <summary>
    /// Create a new draft version of a document
    /// </summary>
    public async Task<LegalDocument> CreateVersionAsync(LegalDocumentType type, string title, string content)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        // Get next version number
        var maxVersion = await context.LegalDocuments
            .Where(d => d.Type == type)
            .MaxAsync(d => (int?)d.Version) ?? 0;

        var document = new LegalDocument
        {
            Type = type,
            Version = maxVersion + 1,
            Title = title,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            IsActive = false
        };

        context.LegalDocuments.Add(document);
        await context.SaveChangesAsync();
        return document;
    }

    /// <summary>
    /// Update an existing draft document
    /// </summary>
    public async Task<bool> UpdateDraftAsync(int id, string title, string content)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var document = await context.LegalDocuments.FindAsync(id);
        
        if (document == null || document.IsActive)
            return false;

        document.Title = title;
        document.Content = content;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Publish a document version, making it the active version
    /// </summary>
    public async Task<bool> PublishVersionAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var document = await context.LegalDocuments.FindAsync(id);
        
        if (document == null)
            return false;

        // Deactivate all other versions of this type
        var otherVersions = await context.LegalDocuments
            .Where(d => d.Type == document.Type && d.IsActive)
            .ToListAsync();
        
        foreach (var version in otherVersions)
        {
            version.IsActive = false;
        }

        // Activate this version
        document.IsActive = true;
        document.PublishedAt = DateTime.UtcNow;
        
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Delete a draft document (cannot delete active documents)
    /// </summary>
    public async Task<bool> DeleteDraftAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var document = await context.LegalDocuments.FindAsync(id);
        
        if (document == null || document.IsActive)
            return false;

        context.LegalDocuments.Remove(document);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Check if a user needs to re-accept legal documents
    /// </summary>
    public async Task<bool> RequiresReacceptanceAsync(string userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var user = await context.Users.FindAsync(userId);
        
        if (user == null)
            return true;

        // Get active versions
        var activeTerms = await GetActiveDocumentAsync(LegalDocumentType.TermsOfService);
        var activePrivacy = await GetActiveDocumentAsync(LegalDocumentType.PrivacyPolicy);

        // If no active documents yet, fall back to legacy check
        if (activeTerms == null && activePrivacy == null)
        {
            return !user.HasAcceptedLegalTerms;
        }

        // Check if user has accepted current versions
        if (activeTerms != null && user.TermsAcceptedVersion != activeTerms.Version)
            return true;
        
        if (activePrivacy != null && user.PrivacyAcceptedVersion != activePrivacy.Version)
            return true;

        return false;
    }

    /// <summary>
    /// Get which documents need re-acceptance
    /// </summary>
    public async Task<ReacceptanceRequirements> GetReacceptanceRequirementsAsync(string userId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var user = await context.Users.FindAsync(userId);
        
        var requirements = new ReacceptanceRequirements();

        if (user == null)
        {
            requirements.NeedsTerms = true;
            requirements.NeedsPrivacy = true;
            return requirements;
        }

        var activeTerms = await GetActiveDocumentAsync(LegalDocumentType.TermsOfService);
        var activePrivacy = await GetActiveDocumentAsync(LegalDocumentType.PrivacyPolicy);

        // Check Terms
        if (activeTerms != null)
        {
            requirements.ActiveTermsVersion = activeTerms.Version;
            requirements.ActiveTermsDocument = activeTerms;
            requirements.NeedsTerms = user.TermsAcceptedVersion != activeTerms.Version;
            requirements.IsTermsUpdate = user.TermsAcceptedVersion.HasValue && requirements.NeedsTerms;
        }
        else
        {
            // Legacy mode - check timestamp only
            requirements.NeedsTerms = !user.TermsAcceptedAt.HasValue;
        }

        // Check Privacy
        if (activePrivacy != null)
        {
            requirements.ActivePrivacyVersion = activePrivacy.Version;
            requirements.ActivePrivacyDocument = activePrivacy;
            requirements.NeedsPrivacy = user.PrivacyAcceptedVersion != activePrivacy.Version;
            requirements.IsPrivacyUpdate = user.PrivacyAcceptedVersion.HasValue && requirements.NeedsPrivacy;
        }
        else
        {
            // Legacy mode - check timestamp only
            requirements.NeedsPrivacy = !user.PrivacyAcceptedAt.HasValue;
        }

        return requirements;
    }

    /// <summary>
    /// Record user acceptance of a legal document
    /// </summary>
    public async Task RecordAcceptanceAsync(string userId, LegalDocumentType type, int version)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var user = await context.Users.FindAsync(userId);
        
        if (user == null) return;

        if (type == LegalDocumentType.TermsOfService)
        {
            user.TermsAcceptedAt = DateTime.UtcNow;
            user.TermsAcceptedVersion = version;
        }
        else if (type == LegalDocumentType.PrivacyPolicy)
        {
            user.PrivacyAcceptedAt = DateTime.UtcNow;
            user.PrivacyAcceptedVersion = version;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Seed initial documents from static content (migration helper)
    /// </summary>
    public async Task SeedInitialDocumentsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        // Check if any documents exist
        if (await context.LegalDocuments.AnyAsync())
            return;

        // Seed Terms of Service
        var terms = new LegalDocument
        {
            Type = LegalDocumentType.TermsOfService,
            Version = 1,
            Title = LegalContent.TermsOfServiceTitle,
            Content = LegalContent.TermsOfService,
            CreatedAt = DateTime.UtcNow,
            PublishedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.LegalDocuments.Add(terms);

        // Seed Privacy Policy
        var privacy = new LegalDocument
        {
            Type = LegalDocumentType.PrivacyPolicy,
            Version = 1,
            Title = LegalContent.PrivacyStatementTitle,
            Content = LegalContent.PrivacyStatement,
            CreatedAt = DateTime.UtcNow,
            PublishedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.LegalDocuments.Add(privacy);

        await context.SaveChangesAsync();
    }
}

/// <summary>
/// Requirements for document re-acceptance
/// </summary>
public class ReacceptanceRequirements
{
    public bool NeedsTerms { get; set; }
    public bool NeedsPrivacy { get; set; }
    public bool IsTermsUpdate { get; set; }
    public bool IsPrivacyUpdate { get; set; }
    public int? ActiveTermsVersion { get; set; }
    public int? ActivePrivacyVersion { get; set; }
    public LegalDocument? ActiveTermsDocument { get; set; }
    public LegalDocument? ActivePrivacyDocument { get; set; }
    
    public bool NeedsAnyAcceptance => NeedsTerms || NeedsPrivacy;
    public bool IsAnyUpdate => IsTermsUpdate || IsPrivacyUpdate;
}
