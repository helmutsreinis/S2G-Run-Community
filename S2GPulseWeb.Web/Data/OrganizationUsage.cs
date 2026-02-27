namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Entity tracking usage metrics per organization.
/// Provides isolated quota tracking separate from personal user quotas.
/// Counters reset monthly based on PeriodStart.
/// </summary>
public class OrganizationUsage
{
    public int Id { get; set; }
    
    /// <summary>
    /// FK to Organization.Id
    /// </summary>
    public Guid OrganizationId { get; set; }
    
    /// <summary>
    /// Navigation property to the organization
    /// </summary>
    public Organization Organization { get; set; } = null!;
    
    // ============================================
    // Storage metrics (bytes) - Organization's isolated storage
    // ============================================
    
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
    /// Total size of organization blob storage (S2G Storage container: org-{guid})
    /// </summary>
    public long StorageBytesBlobStorage { get; set; }
    
    /// <summary>
    /// Total storage in bytes (computed property)
    /// </summary>
    public long TotalStorageBytes => StorageBytesWorkflows + StorageBytesLogs + 
                                     StorageBytesVectors + StorageBytesTables + 
                                     StorageBytesBlobStorage;
    
    // ============================================
    // Execution metrics (reset monthly)
    // ============================================
    
    /// <summary>
    /// Number of workflow executions this billing period
    /// </summary>
    public int ExecutionsThisMonth { get; set; }
    
    // ============================================
    // Billing period tracking
    // ============================================
    
    /// <summary>
    /// Start of current billing period (resets monthly counters)
    /// </summary>
    public DateTime PeriodStart { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Last time usage was updated
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
