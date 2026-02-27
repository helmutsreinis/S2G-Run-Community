using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class SqlNode : BaseNodeExecutor
{
    public SqlNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "SqlServer";

    public override List<string> GetOutputParameters() => new() 
    { 
        "Rows",           // List of row dictionaries
        "RowsJson",       // JSON string of all rows
        "RowsXml",        // XML representation of rows
        "RowsHtml",       // HTML table representation
        "FirstRow",       // First row as dictionary (for single-row queries)
        "FirstRowJson",   // First row as JSON string
        "Count",          // Number of rows returned
        "RowsAffected",   // For INSERT/UPDATE/DELETE queries
        "Columns"         // List of column names
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<SqlNodeConfig>(node.Configuration ?? "{}") ?? new();
        
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            return new NodeExecutionResult { Success = false, ErrorMessage = "Connection string is missing" };
        }

        if (string.IsNullOrEmpty(config.Query))
        {
            return new NodeExecutionResult { Success = false, ErrorMessage = "SQL query is missing" };
        }

        // Resolve placeholders in query
        var resolvedQuery = ResolvePlaceholders(config.Query, inputData);

        using var connection = new SqlConnection(config.ConnectionString);
        await connection.OpenAsync();

        Log(node, NodeLogLevel.Info, "Connected to SQL Server", $"Executing: {TruncateForLog(resolvedQuery, 200)}");

        using var command = new SqlCommand(resolvedQuery, connection);
        command.CommandTimeout = config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 30;
        
        // Add parameters from config (user-defined in the editor)
        if (config.Parameters != null && config.Parameters.Count > 0)
        {
            foreach (var param in config.Parameters)
            {
                // Resolve placeholder values from upstream data
                var valueStr = param.Value ?? "";
                if (valueStr.StartsWith("{{") && valueStr.EndsWith("}}"))
                {
                    var placeholder = valueStr[2..^2];
                    if (inputData.TryGetValue(placeholder, out var resolved) && resolved != null)
                    {
                        valueStr = resolved.ToString() ?? "";
                    }
                    else
                    {
                        // Try short key match
                        var shortKey = placeholder.Contains('.') ? placeholder.Split('.').Last() : placeholder;
                        var match = inputData.FirstOrDefault(kvp => 
                            kvp.Key.EndsWith("." + shortKey) || kvp.Key == shortKey);
                        if (match.Value != null)
                        {
                            valueStr = match.Value.ToString() ?? "";
                        }
                    }
                }
                
                // Convert to appropriate SQL type
                object sqlValue = param.Type switch
                {
                    "Int" => int.TryParse(valueStr, out var i) ? i : DBNull.Value,
                    "Decimal" => decimal.TryParse(valueStr, out var d) ? d : DBNull.Value,
                    "DateTime" => DateTime.TryParse(valueStr, out var dt) ? dt : DBNull.Value,
                    "Boolean" => bool.TryParse(valueStr, out var b) ? b : DBNull.Value,
                    _ => string.IsNullOrEmpty(valueStr) ? DBNull.Value : valueStr // String
                };
                
                command.Parameters.AddWithValue($"@{param.Name}", sqlValue);
            }
        }
        else
        {
            // Fallback: Add parameters from inputData for parameterized queries (@paramName format)
            foreach (var input in inputData)
            {
                var paramName = input.Key.Contains('.') ? input.Key.Split('.').Last() : input.Key;
                
                if (resolvedQuery.Contains($"@{paramName}") || resolvedQuery.Contains($"@{input.Key}"))
                {
                    command.Parameters.AddWithValue($"@{paramName}", input.Value ?? DBNull.Value);
                }
            }
        }

        try
        {
            using var reader = await command.ExecuteReaderAsync();
            var results = new List<Dictionary<string, object?>>();
            var columns = new List<string>();
            
            // Get column names
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }
            
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    // Convert special types to string for JSON compatibility
                    if (value is DateTime dt)
                        value = dt.ToString("yyyy-MM-dd HH:mm:ss");
                    else if (value is byte[] bytes)
                        value = Convert.ToBase64String(bytes);
                    
                    row[reader.GetName(i)] = value;
                }
                results.Add(row);
            }

            Log(node, NodeLogLevel.Info, "Query executed successfully", $"{results.Count} rows returned. Columns: {string.Join(", ", columns)}");

            // Build output data
            var outputData = new Dictionary<string, object?>
            {
                { "Rows", results },
                { "RowsJson", JsonSerializer.Serialize(results) },
                { "RowsXml", GenerateXml(results, columns) },
                { "RowsHtml", GenerateHtml(results, columns) },
                { "Count", results.Count },
                { "Columns", columns },
                { "RowsAffected", reader.RecordsAffected },
                { "_DetectedColumns", string.Join(",", columns) } // For Designer to update config
            };

            // Add first row data for easy access
            if (results.Count > 0)
            {
                var firstRow = results[0];
                outputData["FirstRow"] = firstRow;
                outputData["FirstRowJson"] = JsonSerializer.Serialize(firstRow);
                
                // Add each column of first row as individual output (for single-row queries)
                foreach (var kvp in firstRow)
                {
                    // Use column name directly as output key
                    if (!outputData.ContainsKey(kvp.Key))
                    {
                        outputData[kvp.Key] = kvp.Value;
                    }
                }
            }

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = outputData
            };
        }
        catch (SqlException ex)
        {
            Log(node, NodeLogLevel.Error, "SQL Error", ex.Message);
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"SQL Error: {ex.Message}"
            };
        }
    }

    private string ResolvePlaceholders(string query, Dictionary<string, object?> inputData)
    {
        var result = query;
        
        foreach (var kvp in inputData)
        {
            if (kvp.Value == null) continue;
            
            var valueStr = kvp.Value.ToString() ?? "";
            
            // Replace {key} format placeholders
            result = result.Replace($"{{{kvp.Key}}}", valueStr);
            
            // Also handle short key (without node prefix)
            if (kvp.Key.Contains('.'))
            {
                var shortKey = kvp.Key.Split('.').Last();
                result = result.Replace($"{{{shortKey}}}", valueStr);
            }
        }
        
        return result;
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static string GenerateXml(List<Dictionary<string, object?>> rows, List<string> columns)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<Result>");
        
        foreach (var row in rows)
        {
            sb.AppendLine("  <Row>");
            foreach (var col in columns)
            {
                var value = row.TryGetValue(col, out var v) ? v?.ToString() ?? "" : "";
                var escapedValue = System.Security.SecurityElement.Escape(value);
                sb.AppendLine($"    <{col}>{escapedValue}</{col}>");
            }
            sb.AppendLine("  </Row>");
        }
        
        sb.AppendLine("</Result>");
        return sb.ToString();
    }

    private static string GenerateHtml(List<Dictionary<string, object?>> rows, List<string> columns)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<table class=\"sql-result-table\" style=\"border-collapse: collapse; width: 100%;\">");
        
        // Header row
        sb.AppendLine("  <thead><tr>");
        foreach (var col in columns)
        {
            sb.AppendLine($"    <th style=\"border: 1px solid #ccc; padding: 8px; background: #f5f5f5;\">{System.Net.WebUtility.HtmlEncode(col)}</th>");
        }
        sb.AppendLine("  </tr></thead>");
        
        // Data rows
        sb.AppendLine("  <tbody>");
        foreach (var row in rows)
        {
            sb.AppendLine("    <tr>");
            foreach (var col in columns)
            {
                var value = row.TryGetValue(col, out var v) ? v?.ToString() ?? "" : "";
                sb.AppendLine($"      <td style=\"border: 1px solid #ccc; padding: 8px;\">{System.Net.WebUtility.HtmlEncode(value)}</td>");
            }
            sb.AppendLine("    </tr>");
        }
        sb.AppendLine("  </tbody>");
        
        sb.AppendLine("</table>");
        return sb.ToString();
    }
}

public class SqlNodeConfig
{
    public string? ConnectionString { get; set; }
    public string? Query { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string? ExpectedColumns { get; set; }
    /// <summary>Auto-detected column names from last query execution</summary>
    public string? LastExecutionColumns { get; set; }
    /// <summary>Extracted SQL parameters with values</summary>
    public List<SqlParameter>? Parameters { get; set; }
}

/// <summary>
/// Represents a SQL parameter extracted from the query.
/// </summary>
public class SqlParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "String";  // String, Int, Decimal, DateTime, Boolean
    public string? Value { get; set; }
}

