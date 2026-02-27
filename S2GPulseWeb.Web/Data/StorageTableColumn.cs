namespace S2GPulseWeb.Web.Data;

/// <summary>
/// Defines a column/field schema for a Storage Table node.
/// </summary>
public class StorageTableColumn
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// The WorkflowNode.Id of the Storage Table node this column belongs to.
    /// </summary>
    public Guid StorageTableNodeId { get; set; }
    
    /// <summary>
    /// The name of the column.
    /// </summary>
    public string ColumnName { get; set; } = string.Empty;
    
    /// <summary>
    /// The data type of the column: String, DateTime, Int, Boolean, Double, Guid
    /// </summary>
    public string ColumnType { get; set; } = "String";
    
    /// <summary>
    /// Display order of the column.
    /// </summary>
    public int OrderIndex { get; set; }
}
