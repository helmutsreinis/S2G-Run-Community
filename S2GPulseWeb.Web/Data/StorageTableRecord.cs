namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Stores individual records for a Storage Table node.
/// </summary>
public class StorageTableRecord
{
    /// <summary>
    /// Unique identifier for this record.
    /// </summary>
    public Guid Id { get; set; }
    
    /// <summary>
    /// The WorkflowNode.Id of the Storage Table node this record belongs to.
    /// </summary>
    public Guid StorageTableNodeId { get; set; }
    
    /// <summary>
    /// The user ID who owns this record.
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>
    /// Automatic timestamp when the record was created/updated.
    /// This is the hardcoded Timestamp column that can be queried and used for retention.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Column values stored as JSON: {"ColumnName": value, ...}
    /// </summary>
    public string DataJson { get; set; } = "{}";
}
