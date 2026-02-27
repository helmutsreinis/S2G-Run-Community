using System.Numerics.Tensors;
using System.Text;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for Vector Database CRUD operations and similarity search.
/// </summary>
public class VectorDbService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly UsageTrackingService _usageTrackingService;

    public VectorDbService(IDbContextFactory<ApplicationDbContext> dbContextFactory, UsageTrackingService usageTrackingService)
    {
        _dbContextFactory = dbContextFactory;
        _usageTrackingService = usageTrackingService;
    }

    #region Store Operations

    /// <summary>
    /// Store a document with its embedding vector.
    /// Returns (Success, DocumentId, ErrorMessage) - ErrorMessage is set if storage limit exceeded.
    /// </summary>
    public async Task<(bool Success, Guid DocumentId, string? Error)> StoreAsync(
        Guid vectorDbNodeId, 
        string userId, 
        string text, 
        float[] embedding, 
        string? metadata = null)
    {
        // Check storage limit before storing (uses cached check)
        var (canStore, reason) = await _usageTrackingService.CanStoreAsync(userId);
        if (!canStore)
        {
            return (false, Guid.Empty, reason);
        }
        
        // Check vector document count limit
        var (canAddDoc, currentCount, limit) = await _usageTrackingService.CanAddVectorDocAsync(userId);
        if (!canAddDoc)
        {
            return (false, Guid.Empty, $"Vector document limit reached ({currentCount}/{limit}). Upgrade your plan for more storage.");
        }
        
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var document = new VectorDocument
        {
            Id = Guid.NewGuid(),
            VectorDbNodeId = vectorDbNodeId,
            UserId = userId,
            TextContent = text,
            Embedding = FloatArrayToBytes(embedding),
            EmbeddingDimensions = embedding.Length,
            CreatedAt = DateTime.UtcNow,
            Metadata = metadata
        };

        context.VectorDocuments.Add(document);
        await context.SaveChangesAsync();
        
        // Track storage usage: text + embedding bytes + metadata + overhead
        var vectorBytes = Encoding.UTF8.GetByteCount(text) + 
                          (embedding.Length * 4) + // 4 bytes per float
                          Encoding.UTF8.GetByteCount(metadata ?? "") +
                          100; // Overhead
        await _usageTrackingService.UpdateStorageAsync(userId, vectorBytes: vectorBytes);
        
        // Invalidate cache after storage update so next check reflects new usage
        _usageTrackingService.InvalidateStorageLimitCache(userId);
        
        return (true, document.Id, null);
    }

    #endregion

    #region Search Operations

    /// <summary>
    /// Search for similar documents using cosine similarity.
    /// Uses in-memory computation with TensorPrimitives for hardware acceleration.
    /// </summary>
    public async Task<List<VectorSearchResult>> SearchAsync(
        Guid vectorDbNodeId, 
        float[] queryVector, 
        int limit = 3)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        // Pull all vectors into memory (efficient for local SQLite)
        var documents = await context.VectorDocuments
            .Where(d => d.VectorDbNodeId == vectorDbNodeId)
            .ToListAsync();

        if (!documents.Any())
            return new List<VectorSearchResult>();

        var querySpan = new ReadOnlySpan<float>(queryVector);
        var results = new List<VectorSearchResult>();

        foreach (var doc in documents)
        {
            var docVector = BytesToFloatArray(doc.Embedding, doc.EmbeddingDimensions);
            
            // Skip if dimensions don't match
            if (docVector.Length != queryVector.Length)
                continue;

            // Calculate cosine similarity using hardware acceleration
            float similarity = TensorPrimitives.CosineSimilarity(querySpan, docVector);

            results.Add(new VectorSearchResult
            {
                DocumentId = doc.Id,
                Text = doc.TextContent,
                Similarity = similarity,
                Metadata = doc.Metadata,
                CreatedAt = doc.CreatedAt
            });
        }

        // Sort by similarity (highest first) and take top N
        return results
            .OrderByDescending(x => x.Similarity)
            .Take(limit)
            .ToList();
    }

    #endregion

    #region Delete Operations

    /// <summary>
    /// Delete a specific document by ID.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid documentId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var document = await context.VectorDocuments.FindAsync(documentId);
        if (document == null)
            return false;

        context.VectorDocuments.Remove(document);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Clear all documents for a VectorDb node.
    /// </summary>
    public async Task<int> ClearAsync(Guid vectorDbNodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var documents = await context.VectorDocuments
            .Where(d => d.VectorDbNodeId == vectorDbNodeId)
            .ToListAsync();

        context.VectorDocuments.RemoveRange(documents);
        await context.SaveChangesAsync();
        return documents.Count;
    }
    
    /// <summary>
    /// Clear all vector documents for a user.
    /// </summary>
    public async Task<(int Count, long BytesFreed)> ClearAllForUserAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        
        var documents = await context.VectorDocuments
            .Where(d => d.UserId == userId)
            .ToListAsync();
        
        // Calculate bytes to free
        long bytesFreed = documents.Sum(d => 
            Encoding.UTF8.GetByteCount(d.TextContent) + 
            (d.EmbeddingDimensions * 4) + 
            Encoding.UTF8.GetByteCount(d.Metadata ?? "") + 
            100);

        context.VectorDocuments.RemoveRange(documents);
        await context.SaveChangesAsync();
        
        // Update storage tracking
        if (documents.Any())
        {
            await _usageTrackingService.UpdateStorageAsync(userId, vectorBytes: -bytesFreed);
        }
        
        return (documents.Count, bytesFreed);
    }

    #endregion

    #region Query Operations

    /// <summary>
    /// Get document count for a VectorDb node.
    /// </summary>
    public async Task<int> GetCountAsync(Guid vectorDbNodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.VectorDocuments
            .Where(d => d.VectorDbNodeId == vectorDbNodeId)
            .CountAsync();
    }

    /// <summary>
    /// Get all documents for a VectorDb node.
    /// </summary>
    public async Task<List<VectorDocument>> GetAllAsync(Guid vectorDbNodeId, int maxResults = 100)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.VectorDocuments
            .Where(d => d.VectorDbNodeId == vectorDbNodeId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(maxResults)
            .ToListAsync();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Convert float array to byte array for storage.
    /// </summary>
    private static byte[] FloatArrayToBytes(float[] floats)
    {
        byte[] bytes = new byte[floats.Length * 4]; // 4 bytes per float
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Convert byte array back to float array.
    /// </summary>
    private static float[] BytesToFloatArray(byte[] bytes, int dimensions)
    {
        float[] floats = new float[dimensions];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    #endregion
}

/// <summary>
/// Result of a vector similarity search.
/// </summary>
public class VectorSearchResult
{
    public Guid DocumentId { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Similarity { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}
