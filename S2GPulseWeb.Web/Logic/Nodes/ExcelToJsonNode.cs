using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ExcelDataReader;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Excel to JSON node - Converts Excel files to JSON arrays with automatic schema detection.
/// Accepts base64-encoded Excel content from FileDownload, S2GStorage, or AzureBlob nodes.
/// </summary>
public class ExcelToJsonNode : BaseNodeExecutor
{
    public ExcelToJsonNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "ExcelToJson";

    public override List<string> GetOutputParameters() => new()
    {
        "Json", "Schema", "SheetNames", "RowCount", "Success", "ErrorMessage"
    };

    // Static constructor to register encoding provider (required by ExcelDataReader)
    static ExcelToJsonNode()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<ExcelToJsonConfig>(node.Configuration ?? "{}") ?? new();

        // Resolve placeholders in content field
        var contentBase64 = ReplacePlaceholders(config.ContentBase64 ?? "", inputData);

        if (string.IsNullOrWhiteSpace(contentBase64))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "ContentBase64 is required. Connect to FileDownload, S2GStorage, or AzureBlob node.",
                OutputData = new Dictionary<string, object?>
                {
                    { "Success", false },
                    { "ErrorMessage", "ContentBase64 is required" }
                }
            };
        }

        try
        {
            // Decode base64 to bytes
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(contentBase64);
            }
            catch (FormatException)
            {
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Invalid base64 content. Ensure you're using ContentBase64 from a storage or download node.",
                    OutputData = new Dictionary<string, object?>
                    {
                        { "Success", false },
                        { "ErrorMessage", "Invalid base64 content" }
                    }
                };
            }

            Log(node, NodeLogLevel.Info, $"Processing Excel file ({bytes.Length} bytes)");

            using var stream = new MemoryStream(bytes);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var result = ProcessExcelReader(reader, config, node);
            return await Task.FromResult(result);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Excel processing error: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                OutputData = new Dictionary<string, object?>
                {
                    { "Success", false },
                    { "ErrorMessage", ex.Message }
                }
            };
        }
    }

    private NodeExecutionResult ProcessExcelReader(IExcelDataReader reader, ExcelToJsonConfig config, WorkflowNode node)
    {
        var allSheets = new Dictionary<string, object>();
        var allSchemas = new Dictionary<string, object>();
        var sheetNames = new List<string>();
        var totalRows = 0;

        do
        {
            var sheetName = reader.Name;

            // Skip if specific sheet requested and this isn't it
            if (!string.IsNullOrEmpty(config.SheetName) &&
                !string.Equals(sheetName, config.SheetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sheetResult = ProcessSheet(reader, config, node);
            if (sheetResult.Rows.Count > 0 || config.IncludeEmptyRows)
            {
                sheetNames.Add(sheetName);
                allSheets[sheetName] = sheetResult.Rows;
                allSchemas[sheetName] = sheetResult.Schema;
                totalRows += sheetResult.Rows.Count;

                Log(node, NodeLogLevel.Info, $"Sheet '{sheetName}': {sheetResult.Rows.Count} rows, {sheetResult.Schema.Count} columns");
            }
        }
        while (reader.NextResult());

        // If only one sheet, simplify output
        object jsonOutput;
        object schemaOutput;
        if (sheetNames.Count == 1)
        {
            jsonOutput = allSheets[sheetNames[0]];
            schemaOutput = allSchemas[sheetNames[0]];
        }
        else
        {
            jsonOutput = allSheets;
            schemaOutput = allSchemas;
        }

        var jsonString = JsonSerializer.Serialize(jsonOutput, new JsonSerializerOptions { WriteIndented = false });

        Log(node, NodeLogLevel.Info, $"Converted {sheetNames.Count} sheet(s), {totalRows} total rows");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Json", jsonString },
                { "Schema", JsonSerializer.Serialize(schemaOutput) },
                { "SheetNames", JsonSerializer.Serialize(sheetNames) },
                { "RowCount", totalRows },
                { "Success", true },
                { "ErrorMessage", null }
            }
        };
    }

    private (List<Dictionary<string, object?>> Rows, List<ColumnSchema> Schema) ProcessSheet(
        IExcelDataReader reader, ExcelToJsonConfig config, WorkflowNode node)
    {
        var rows = new List<Dictionary<string, object?>>();
        var schema = new List<ColumnSchema>();
        var headerRow = -1;
        var headers = new List<string>();
        var rowIndex = 0;

        // First pass: find header row and read all data
        var allRows = new List<object?[]>();
        while (reader.Read())
        {
            var rowData = new object?[reader.FieldCount];
            var hasData = false;

            for (int col = 0; col < reader.FieldCount; col++)
            {
                var value = reader.GetValue(col);
                rowData[col] = value;
                if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                {
                    hasData = true;
                }
            }

            allRows.Add(rowData);

            // Detect header row: first row with data within detection range
            if (headerRow < 0 && hasData && rowIndex < config.HeaderDetectionRows)
            {
                headerRow = rowIndex;
            }

            rowIndex++;
        }

        if (headerRow < 0 || allRows.Count <= headerRow)
        {
            return (rows, schema); // No data found
        }

        // Extract headers from header row
        var headerRowData = allRows[headerRow];
        for (int col = 0; col < headerRowData.Length; col++)
        {
            var headerValue = headerRowData[col]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(headerValue))
            {
                headerValue = $"Column{col + 1}";
            }

            // Handle duplicate headers by appending suffix
            var baseName = headerValue;
            var suffix = 1;
            while (headers.Contains(headerValue))
            {
                suffix++;
                headerValue = $"{baseName}_{suffix}";
            }

            headers.Add(headerValue);
        }

        // Infer types from first N data rows
        var typeInferenceEnd = Math.Min(headerRow + 1 + config.TypeInferenceRows, allRows.Count);
        var columnTypes = InferColumnTypes(allRows, headerRow + 1, typeInferenceEnd, headers.Count);

        // Build schema
        for (int col = 0; col < headers.Count; col++)
        {
            schema.Add(new ColumnSchema
            {
                Name = headers[col],
                Type = columnTypes[col],
                Index = col
            });
        }

        // Convert data rows to dictionaries
        for (int r = headerRow + 1; r < allRows.Count; r++)
        {
            var rowData = allRows[r];
            var hasData = false;

            // Check if row has any data
            for (int col = 0; col < Math.Min(rowData.Length, headers.Count); col++)
            {
                if (rowData[col] != null && !string.IsNullOrWhiteSpace(rowData[col]?.ToString()))
                {
                    hasData = true;
                    break;
                }
            }

            if (!hasData && !config.IncludeEmptyRows)
            {
                continue;
            }

            var dict = new Dictionary<string, object?>();
            for (int col = 0; col < headers.Count; col++)
            {
                var value = col < rowData.Length ? rowData[col] : null;
                dict[headers[col]] = ConvertValue(value, columnTypes[col]);
            }

            rows.Add(dict);
        }

        return (rows, schema);
    }

    private List<string> InferColumnTypes(List<object?[]> allRows, int startRow, int endRow, int columnCount)
    {
        var types = new List<string>();

        for (int col = 0; col < columnCount; col++)
        {
            var inferredType = "String"; // Default
            var hasNumber = false;
            var hasDate = false;
            var hasBool = false;
            var sampleCount = 0;

            for (int r = startRow; r < endRow && r < allRows.Count; r++)
            {
                if (col >= allRows[r].Length) continue;

                var value = allRows[r][col];
                if (value == null) continue;

                sampleCount++;

                if (value is DateTime)
                {
                    hasDate = true;
                }
                else if (value is bool)
                {
                    hasBool = true;
                }
                else if (value is double || value is float || value is int || value is long || value is decimal)
                {
                    hasNumber = true;
                }
                else if (value is string strVal)
                {
                    // Try parsing as number
                    if (double.TryParse(strVal, out _))
                    {
                        hasNumber = true;
                    }
                    else if (DateTime.TryParse(strVal, out _))
                    {
                        hasDate = true;
                    }
                    else if (bool.TryParse(strVal, out _))
                    {
                        hasBool = true;
                    }
                }
            }

            // Determine type based on what we found
            if (sampleCount > 0)
            {
                if (hasDate && !hasNumber && !hasBool)
                    inferredType = "Date";
                else if (hasNumber && !hasDate && !hasBool)
                    inferredType = "Number";
                else if (hasBool && !hasNumber && !hasDate)
                    inferredType = "Boolean";
                // else mixed or string - keep as String
            }

            types.Add(inferredType);
        }

        return types;
    }

    private object? ConvertValue(object? value, string targetType)
    {
        if (value == null) return null;

        try
        {
            return targetType switch
            {
                "Number" => value switch
                {
                    double d => d,
                    float f => (double)f,
                    int i => (double)i,
                    long l => (double)l,
                    decimal m => (double)m,
                    string s when double.TryParse(s, out var n) => n,
                    _ => value.ToString()
                },
                "Date" => value switch
                {
                    DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss"),
                    string s when DateTime.TryParse(s, out var dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss"),
                    _ => value.ToString()
                },
                "Boolean" => value switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out var b) => b,
                    _ => value.ToString()
                },
                _ => value.ToString()
            };
        }
        catch
        {
            return value.ToString();
        }
    }

    private string ReplacePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        foreach (var kvp in data)
        {
            template = template.Replace($"{{{{{kvp.Key}}}}}", kvp.Value?.ToString() ?? "");
        }
        return template;
    }
}

/// <summary>
/// Configuration for Excel to JSON conversion.
/// </summary>
public class ExcelToJsonConfig
{
    /// <summary>Base64-encoded Excel file content from FileDownload, S2GStorage, or AzureBlob.</summary>
    public string? ContentBase64 { get; set; }

    /// <summary>Optional: specific sheet name to process (empty = all sheets).</summary>
    public string? SheetName { get; set; }

    /// <summary>Number of rows to scan for header detection (default: 10).</summary>
    public int HeaderDetectionRows { get; set; } = 10;

    /// <summary>Number of data rows to sample for type inference (default: 4).</summary>
    public int TypeInferenceRows { get; set; } = 4;

    /// <summary>Whether to include empty rows in output (default: false).</summary>
    public bool IncludeEmptyRows { get; set; } = false;
}

/// <summary>
/// Schema information for a column.
/// </summary>
public class ColumnSchema
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "String";
    public int Index { get; set; }
}
