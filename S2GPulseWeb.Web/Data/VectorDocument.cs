namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Entity for storing text documents with their vector embeddings.
/// Used by VectorDb nodes for similarity search.
/// </summary>
public class VectorDocument
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// The VectorDb node that owns this document.
    /// </summary>
    public Guid VectorDbNodeId { get; set; }
    
    /// <summary>
    /// User who created the document.
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>
    /// The original text content.
    /// </summary>
    public string TextContent { get; set; } = string.Empty;
    
    /// <summary>
    /// The vector embedding stored as raw bytes (float[] serialized).
    /// </summary>
    public byte[] Embedding { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// Number of dimensions in the embedding vector.
    /// </summary>
    public int EmbeddingDimensions { get; set; }
    
    /// <summary>
    /// When the document was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Optional JSON metadata associated with the document.
    /// </summary>
    public string? Metadata { get; set; }
}
