namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Entity tracking monthly usage metrics per user
/// Counters reset monthly based on PeriodStart
/// </summary>
public class UserUsage
{
    public int Id { get; set; }
    
    /// <summary>
    /// FK to ApplicationUser.Id
    /// </summary>
    public string UserId { get; set; } = null!;
    
    // Storage metrics (bytes)
    
    /// <summary>
    /// Total size of workflow JSON definitions
    /// </summary>
    public long StorageBytesWorkflows { get; set; }
    
    /// <summary>
    /// Total size of node logs
    /// </summary>
    public long StorageBytesLogs { get; set; }
    
    /// <summary>
    /// Total size of vector documents (text + embeddings)
    /// </summary>
    public long StorageBytesVectors { get; set; }
    
    /// <summary>
    /// Total size of storage table records
    /// </summary>
    public long StorageBytesTables { get; set; }
    
    /// <summary>
    /// Total size of S2G personal blob storage
    /// </summary>
    public long StorageBytesBlobStorage { get; set; }
    
    /// <summary>
    /// Total storage in bytes (computed)
    /// </summary>
    public long TotalStorageBytes => StorageBytesWorkflows + StorageBytesLogs + StorageBytesVectors + StorageBytesTables + StorageBytesBlobStorage;
    
    // Execution metrics (reset monthly)
    
    /// <summary>
    /// Number of workflow executions this billing period
    /// </summary>
    public int ExecutionsThisMonth { get; set; }
    
    // Billing period tracking
    
    /// <summary>
    /// Start of current billing period (resets monthly counters)
    /// </summary>
    public DateTime PeriodStart { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Last time usage was updated
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Admin-set override for execution limit. If set, overrides tier-based limit.
    /// </summary>
    public int? ExecutionLimitOverride { get; set; }
    
    /// <summary>
    /// Admin-set override for storage limit in bytes. If set, overrides tier-based limit.
    /// </summary>
    public long? StorageLimitOverride { get; set; }
}
