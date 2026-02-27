using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing developer notes and announcements
/// </summary>
public class DeveloperNoteService
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

    public DeveloperNoteService(IDbContextFactory<ApplicationDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Get all published notes ordered by display order
    /// </summary>
    public async Task<List<DeveloperNote>> GetPublishedNotesAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.DeveloperNotes
            .Where(n => n.IsPublished)
            .OrderByDescending(n => n.DisplayOrder)
            .ThenByDescending(n => n.PublishedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get all notes for admin management
    /// </summary>
    public async Task<List<DeveloperNote>> GetAllNotesAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.DeveloperNotes
            .OrderByDescending(n => n.DisplayOrder)
            .ThenByDescending(n => n.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get a specific note by ID
    /// </summary>
    public async Task<DeveloperNote?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.DeveloperNotes.FindAsync(id);
    }

    /// <summary>
    /// Create a new developer note
    /// </summary>
    public async Task<DeveloperNote> CreateNoteAsync(string title, string content, bool publish = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        // Get next display order
        var maxOrder = await context.DeveloperNotes.MaxAsync(n => (int?)n.DisplayOrder) ?? 0;

        var note = new DeveloperNote
        {
            Title = title,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            IsPublished = publish,
            PublishedAt = publish ? DateTime.UtcNow : null,
            DisplayOrder = maxOrder + 1
        };

        context.DeveloperNotes.Add(note);
        await context.SaveChangesAsync();
        return note;
    }

    /// <summary>
    /// Update an existing note
    /// </summary>
    public async Task<bool> UpdateNoteAsync(int id, string title, string content)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var note = await context.DeveloperNotes.FindAsync(id);
        
        if (note == null)
            return false;

        note.Title = title;
        note.Content = content;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Toggle publish status of a note
    /// </summary>
    public async Task<bool> TogglePublishAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var note = await context.DeveloperNotes.FindAsync(id);
        
        if (note == null)
            return false;

        note.IsPublished = !note.IsPublished;
        if (note.IsPublished && !note.PublishedAt.HasValue)
        {
            note.PublishedAt = DateTime.UtcNow;
        }
        
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Update display order of a note
    /// </summary>
    public async Task<bool> UpdateDisplayOrderAsync(int id, int newOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var note = await context.DeveloperNotes.FindAsync(id);
        
        if (note == null)
            return false;

        note.DisplayOrder = newOrder;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Delete a note
    /// </summary>
    public async Task<bool> DeleteNoteAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var note = await context.DeveloperNotes.FindAsync(id);
        
        if (note == null)
            return false;

        context.DeveloperNotes.Remove(note);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Toggle newsletter status of a note
    /// </summary>
    public async Task<bool> ToggleNewsletterAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var note = await context.DeveloperNotes.FindAsync(id);
        
        if (note == null)
            return false;

        note.ShowAsNewsletter = !note.ShowAsNewsletter;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Update the target page for a newsletter note
    /// </summary>
    public async Task<bool> UpdateTargetPageAsync(int id, string targetPage)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var note = await context.DeveloperNotes.FindAsync(id);
        
        if (note == null)
            return false;

        note.TargetPage = targetPage;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Get newsletter notes that the user hasn't dismissed yet, filtered by target page
    /// </summary>
    public async Task<List<DeveloperNote>> GetUnseenNewsletterNotesAsync(string userId, string currentPage = "Home")
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        var dismissedNoteIds = await context.UserDismissedNotes
            .Where(d => d.UserId == userId)
            .Select(d => d.NoteId)
            .ToListAsync();

        return await context.DeveloperNotes
            .Where(n => n.IsPublished && n.ShowAsNewsletter && !dismissedNoteIds.Contains(n.Id))
            .Where(n => n.TargetPage == currentPage || n.TargetPage == "All")
            .OrderByDescending(n => n.PublishedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Record that a user has dismissed a newsletter note
    /// </summary>
    public async Task DismissNoteForUserAsync(string userId, int noteId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        // Check if already dismissed
        var existing = await context.UserDismissedNotes
            .FirstOrDefaultAsync(d => d.UserId == userId && d.NoteId == noteId);
        
        if (existing != null)
            return;

        context.UserDismissedNotes.Add(new UserDismissedNote
        {
            UserId = userId,
            NoteId = noteId,
            DismissedAt = DateTime.UtcNow
        });
        
        await context.SaveChangesAsync();
    }
}
