using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class AzureBlobNode : BaseNodeExecutor
{
    public AzureBlobNode(NodeExecutionManager executionManager) : base(executionManager) { }

    public override string NodeType => "AzureBlob";

    public override List<string> GetOutputParameters() => new() 
    { 
        "Content", "BlobPath", "BlobSize", "ContentType", "BytesWritten", "Deleted", "DeletedCount", "Success" 
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node, 
        Dictionary<string, object?> inputData, 
        string userId)
    {
        var config = JsonSerializer.Deserialize<AzureBlobConfig>(node.Configuration ?? "{}") ?? new();
        var operation = config.Operation ?? "Read";

        var connectionString = ReplacePlaceholders(config.ConnectionString ?? "", inputData);
        var containerName = ReplacePlaceholders(config.ContainerName ?? "", inputData);
        var blobPath = ReplacePlaceholders(config.BlobPath ?? "", inputData);
        var content = ReplacePlaceholders(config.Content ?? "", inputData);
        var maxSize = config.MaxFileSizeBytes > 0 ? config.MaxFileSizeBytes : 20 * 1024 * 1024;

        Log(node, NodeLogLevel.Info, $"Starting Azure Blob operation: {operation} on container '{containerName}', blob '{blobPath}'");

        if (string.IsNullOrEmpty(connectionString))
            return new NodeExecutionResult { Success = false, ErrorMessage = "Azure Blob Connection String is missing" };

        if (string.IsNullOrEmpty(containerName))
            return new NodeExecutionResult { Success = false, ErrorMessage = "Container name is missing" };

        if (string.IsNullOrEmpty(blobPath) && operation != "List" && operation != "DeleteFolder")
            return new NodeExecutionResult { Success = false, ErrorMessage = "Blob path is missing" };

        try
        {
            var blobServiceClient = new BlobServiceClient(connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            if (config.CreateContainerIfNotExists)
            {
                await containerClient.CreateIfNotExistsAsync();
            }

            return operation switch
            {
                "Read" => await ReadBlobAsync(node, containerClient, blobPath, maxSize),
                "Write" => await WriteBlobAsync(node, containerClient, blobPath, content, config),
                "Edit" => await WriteBlobAsync(node, containerClient, blobPath, content, config), // Edit is same as Write
                "Delete" => await DeleteBlobAsync(node, containerClient, blobPath),
                "DeleteFolder" => await DeleteFolderAsync(node, containerClient, blobPath),
                "List" => await ListBlobsAsync(node, containerClient),
                _ => new NodeExecutionResult { Success = false, ErrorMessage = $"Unsupported operation: {operation}" }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Azure Blob operation failed: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<NodeExecutionResult> ReadBlobAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string blobPath, 
        long maxSize)
    {
        var blobClient = containerClient.GetBlobClient(blobPath);

        if (!await blobClient.ExistsAsync())
        {
            Log(node, NodeLogLevel.Warning, $"Blob '{blobPath}' does not exist");
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = $"Blob '{blobPath}' does not exist" 
            };
        }

        var properties = await blobClient.GetPropertiesAsync();
        var blobSize = properties.Value.ContentLength;
        var contentType = properties.Value.ContentType;

        // Check file size guardrail
        if (blobSize > maxSize)
        {
            var sizeMB = blobSize / (1024.0 * 1024.0);
            var maxMB = maxSize / (1024.0 * 1024.0);
            Log(node, NodeLogLevel.Warning, $"Blob size ({sizeMB:F2}MB) exceeds maximum allowed ({maxMB:F2}MB)");
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = $"Blob size ({sizeMB:F2}MB) exceeds maximum allowed ({maxMB:F2}MB). Increase the limit or use a different approach for large files." 
            };
        }

        // Warn if approaching limit (80%)
        if (blobSize > maxSize * 0.8)
        {
            Log(node, NodeLogLevel.Warning, $"Blob size is approaching the maximum limit");
        }

        var response = await blobClient.DownloadContentAsync();
        var blobContent = response.Value.Content.ToString();

        Log(node, NodeLogLevel.Info, $"Read {blobSize} bytes from blob '{blobPath}'");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Content", blobContent },
                { "BlobPath", blobPath },
                { "BlobSize", blobSize },
                { "ContentType", contentType },
                { "Success", true }
            }
        };
    }

    private async Task<NodeExecutionResult> WriteBlobAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string blobPath, 
        string content,
        AzureBlobConfig config)
    {
        var blobClient = containerClient.GetBlobClient(blobPath);
        
        // Decode content - prefer explicit ContentBase64, or auto-detect base64 in Content field
        byte[] bytes;
        if (!string.IsNullOrEmpty(config.ContentBase64))
        {
            // Explicit base64 field provided
            try
            {
                bytes = Convert.FromBase64String(config.ContentBase64);
                Log(node, NodeLogLevel.Info, $"Decoded {bytes.Length} bytes from ContentBase64 field");
            }
            catch (FormatException ex)
            {
                return new NodeExecutionResult { Success = false, ErrorMessage = $"Invalid base64 content: {ex.Message}" };
            }
        }
        else if (IsLikelyBase64(content))
        {
            // Auto-detect: Content looks like base64 (from FileDownload node)
            try
            {
                bytes = Convert.FromBase64String(content);
                Log(node, NodeLogLevel.Info, $"Auto-detected base64, decoded {bytes.Length} bytes from Content field");
            }
            catch (FormatException)
            {
                // Not valid base64, treat as text
                bytes = Encoding.UTF8.GetBytes(content);
            }
        }
        else
        {
            // Regular text content
            bytes = Encoding.UTF8.GetBytes(content);
        }

        using var stream = new MemoryStream(bytes);
        await blobClient.UploadAsync(stream, overwrite: true);

        Log(node, NodeLogLevel.Info, $"Wrote {bytes.Length} bytes to blob '{blobPath}'");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "BlobPath", blobPath },
                { "BytesWritten", bytes.Length },
                { "Success", true }
            }
        };
    }

    private async Task<NodeExecutionResult> DeleteBlobAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string blobPath)
    {
        var blobClient = containerClient.GetBlobClient(blobPath);
        var deleted = await blobClient.DeleteIfExistsAsync();

        if (deleted)
        {
            Log(node, NodeLogLevel.Info, $"Deleted blob '{blobPath}'");
        }
        else
        {
            Log(node, NodeLogLevel.Warning, $"Blob '{blobPath}' did not exist");
        }

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "BlobPath", blobPath },
                { "Deleted", deleted.Value },
                { "Success", true }
            }
        };
    }

    /// <summary>
    /// Deletes all blobs under a folder prefix recursively.
    /// </summary>
    private async Task<NodeExecutionResult> DeleteFolderAsync(
        WorkflowNode node,
        BlobContainerClient containerClient,
        string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
            return new NodeExecutionResult { Success = false, ErrorMessage = "Folder/prefix path is required for DeleteFolder operation" };

        var prefix = folderPath.TrimEnd('/') + "/";
        var deletedCount = 0;

        // Collect all blobs to delete
        var blobsToDelete = new List<string>();
        await foreach (var blobItem in containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, default))
        {
            blobsToDelete.Add(blobItem.Name);
        }

        // Delete all collected blobs
        foreach (var blobName in blobsToDelete)
        {
            var blobClient = containerClient.GetBlobClient(blobName);
            var deleted = await blobClient.DeleteIfExistsAsync();
            if (deleted)
            {
                deletedCount++;
            }
        }

        Log(node, NodeLogLevel.Info, $"Deleted folder/prefix '{folderPath}' with {deletedCount} blobs");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "BlobPath", folderPath },
                { "DeletedCount", deletedCount },
                { "Success", true }
            }
        };
    }

    private async Task<NodeExecutionResult> ListBlobsAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient)
    {
        var blobs = new List<object>();
        
        await foreach (var blobItem in containerClient.GetBlobsAsync())
        {
            blobs.Add(new
            {
                Name = blobItem.Name,
                Size = blobItem.Properties.ContentLength,
                ContentType = blobItem.Properties.ContentType,
                LastModified = blobItem.Properties.LastModified
            });
        }

        Log(node, NodeLogLevel.Info, $"Listed {blobs.Count} blobs in container");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Content", JsonSerializer.Serialize(blobs) },
                { "BlobCount", blobs.Count },
                { "Success", true }
            }
        };
    }

    private string ReplacePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        foreach (var kvp in data)
        {
            template = template.Replace($"{{{kvp.Key}}}", kvp.Value?.ToString() ?? "");
        }
        return template;
    }

    /// <summary>
    /// Heuristic to detect if content is likely base64-encoded binary data.
    /// </summary>
    private static bool IsLikelyBase64(string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length < 100)
            return false;

        var trimmed = content.Trim();
        
        // Check if it starts with common text patterns
        if (trimmed.StartsWith("{") || trimmed.StartsWith("[") || 
            trimmed.StartsWith("<") || trimmed.StartsWith("http"))
            return false;

        // Check character composition - base64 uses A-Z, a-z, 0-9, +, /, =
        var validChars = 0;
        var totalChars = Math.Min(trimmed.Length, 500);
        for (var i = 0; i < totalChars; i++)
        {
            var c = trimmed[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || 
                (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
                validChars++;
        }

        return (double)validChars / totalChars > 0.95;
    }
}

/// <summary>
/// Configuration for Azure Blob Storage operations.
/// </summary>
public class AzureBlobConfig
{
    /// <summary>Operation to perform: Read, Write, Edit, Delete, DeleteFolder, List</summary>
    public string? Operation { get; set; } = "Read";
    
    /// <summary>Azure Storage connection string</summary>
    public string? ConnectionString { get; set; }
    
    /// <summary>Name of the blob container</summary>
    public string? ContainerName { get; set; }
    
    /// <summary>Path to the blob for Read/Write/Delete operations (e.g., "folder/file.pdf")</summary>
    public string? BlobPath { get; set; }
    
    /// <summary>
    /// Text content to write for Write/Edit operations.
    /// Use this for plain text files like .txt, .json, .csv.
    /// For binary files, use ContentBase64 instead.
    /// </summary>
    public string? Content { get; set; }
    
    /// <summary>
    /// Base64-encoded binary content for Write/Edit operations.
    /// Takes precedence over Content when both are provided.
    /// Use this for binary files like images, PDFs, ZIPs.
    /// Typically populated from FileDownload node: {{FileDownload.ContentBase64}}
    /// </summary>
    public string? ContentBase64 { get; set; }
    
    /// <summary>Maximum file size allowed for Read operations (default: 20MB)</summary>
    public long MaxFileSizeBytes { get; set; } = 20 * 1024 * 1024; // 20MB default
    
    /// <summary>Automatically create the container if it doesn't exist</summary>
    public bool CreateContainerIfNotExists { get; set; } = true;
}
