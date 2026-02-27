using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// S2G Storage node - Personal and organization file storage backed by Azure Blob Storage.
/// Personal context: user-{userId} container
/// Organization context: org-{organizationId} container
/// Storage is tracked against tier limits for both personal and org quotas.
/// </summary>
public class S2GStorageNode : BaseNodeExecutor
{
    private readonly UsageTrackingService _usageTrackingService;
    private readonly OrganizationUsageTrackingService _orgUsageTrackingService;
    private readonly string _connectionString;

    public S2GStorageNode(
        NodeExecutionManager executionManager, 
        UsageTrackingService usageTrackingService, 
        OrganizationUsageTrackingService orgUsageTrackingService,
        IConfiguration configuration) 
        : base(executionManager) 
    {
        _usageTrackingService = usageTrackingService;
        _orgUsageTrackingService = orgUsageTrackingService;
        _connectionString = configuration["S2GStorage:ConnectionString"] 
            ?? throw new InvalidOperationException("S2GStorage:ConnectionString is not configured");
    }

    public override string NodeType => "S2GStorage";

    public override List<string> GetOutputParameters() => new() 
    { 
        "Content", "FilePath", "FileSize", "ContentType", "BytesWritten", 
        "Deleted", "FileCount", "Success", "FolderName", "UsedStorageBytes",
        "Files", "Folders"
    };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node, 
        Dictionary<string, object?> inputData, 
        string userId)
    {
        var config = JsonSerializer.Deserialize<S2GStorageConfig>(node.Configuration ?? "{}") ?? new();
        var operation = config.Operation ?? "List";

        var filePath = ReplacePlaceholders(config.FilePath ?? "", inputData);
        var folderPath = ReplacePlaceholders(config.FolderPath ?? "", inputData);
        var content = ReplacePlaceholders(config.Content ?? "", inputData);
        var maxSize = config.MaxFileSizeBytes > 0 ? config.MaxFileSizeBytes : 20 * 1024 * 1024;

        // Determine container name based on organization context
        var containerName = GetContainerName(inputData, userId);
        var organizationId = GetOrganizationId(inputData);

        Log(node, NodeLogLevel.Info, $"S2G Storage operation: {operation}" + 
            (organizationId.HasValue ? $" (Organization context: {organizationId})" : " (Personal context)"));

        try
        {
            var blobServiceClient = new BlobServiceClient(_connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            // Always ensure container exists
            await containerClient.CreateIfNotExistsAsync();

            return operation switch
            {
                "List" => await ListFilesAsync(node, containerClient, folderPath),
                "Read" => await ReadFileAsync(node, containerClient, filePath, maxSize),
                "Write" => await WriteFileAsync(node, containerClient, filePath, content, userId, organizationId, config),
                "Edit" => await WriteFileAsync(node, containerClient, filePath, content, userId, organizationId, config),
                "Delete" => await DeleteFileAsync(node, containerClient, filePath, userId, organizationId),
                "DeleteFolder" => await DeleteFolderAsync(node, containerClient, folderPath, userId, organizationId),
                "CreateFolder" => await CreateFolderAsync(node, containerClient, folderPath),
                _ => new NodeExecutionResult { Success = false, ErrorMessage = $"Unsupported operation: {operation}" }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"S2G Storage operation failed: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<NodeExecutionResult> ListFilesAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string folderPath)
    {
        var files = new List<object>();
        var folders = new HashSet<string>();
        
        // Normalize folder path
        var prefix = string.IsNullOrEmpty(folderPath) ? "" : folderPath.TrimEnd('/') + "/";
        
        await foreach (var blobItem in containerClient.GetBlobsAsync())
        {
            var blobName = blobItem.Name;
            
            // Skip blobs outside the current folder
            if (!string.IsNullOrEmpty(prefix) && !blobName.StartsWith(prefix))
                continue;
            
            var relativePath = string.IsNullOrEmpty(prefix) ? blobName : blobName.Substring(prefix.Length);
            
            // Check if this is in a subfolder
            var slashIndex = relativePath.IndexOf('/');
            if (slashIndex > 0)
            {
                // This is in a subfolder - extract folder name
                var folderName = relativePath.Substring(0, slashIndex);
                folders.Add(folderName);
            }
            else if (!string.IsNullOrEmpty(relativePath) && !relativePath.EndsWith(".folder"))
            {
                // This is a file in the current folder (skip .folder placeholder files)
                files.Add(new
                {
                    Name = relativePath,
                    FullPath = blobItem.Name,
                    Size = blobItem.Properties.ContentLength,
                    ContentType = blobItem.Properties.ContentType,
                    LastModified = blobItem.Properties.LastModified
                });
            }
        }

        Log(node, NodeLogLevel.Info, $"Listed {files.Count} files and {folders.Count} folders");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Files", JsonSerializer.Serialize(files) },
                { "Folders", JsonSerializer.Serialize(folders.ToList()) },
                { "FileCount", files.Count },
                { "FolderPath", folderPath },
                { "Success", true }
            }
        };
    }

    private async Task<NodeExecutionResult> ReadFileAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string filePath, 
        long maxSize)
    {
        if (string.IsNullOrEmpty(filePath))
            return new NodeExecutionResult { Success = false, ErrorMessage = "File path is required for Read operation" };

        var blobClient = containerClient.GetBlobClient(filePath);

        if (!await blobClient.ExistsAsync())
        {
            Log(node, NodeLogLevel.Warning, $"File '{filePath}' does not exist");
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = $"File '{filePath}' does not exist" 
            };
        }

        var properties = await blobClient.GetPropertiesAsync();
        var fileSize = properties.Value.ContentLength;
        var contentType = properties.Value.ContentType;

        // Check file size guardrail
        if (fileSize > maxSize)
        {
            var sizeMB = fileSize / (1024.0 * 1024.0);
            var maxMB = maxSize / (1024.0 * 1024.0);
            Log(node, NodeLogLevel.Warning, $"File size ({sizeMB:F2}MB) exceeds maximum allowed ({maxMB:F2}MB)");
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = $"File size ({sizeMB:F2}MB) exceeds maximum allowed ({maxMB:F2}MB)." 
            };
        }

        var response = await blobClient.DownloadContentAsync();
        var fileContent = response.Value.Content.ToString();

        Log(node, NodeLogLevel.Info, $"Read {fileSize} bytes from '{filePath}'");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Content", fileContent },
                { "FilePath", filePath },
                { "FileSize", fileSize },
                { "ContentType", contentType },
                { "Success", true }
            }
        };
    }

    private async Task<NodeExecutionResult> WriteFileAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string filePath, 
        string content,
        string userId,
        Guid? organizationId,
        S2GStorageConfig config)
    {
        if (string.IsNullOrEmpty(filePath))
            return new NodeExecutionResult { Success = false, ErrorMessage = "File path is required for Write operation" };

        // Check storage limit before writing - use org or personal tracking
        (bool canStore, string? reason) = organizationId.HasValue
            ? await _orgUsageTrackingService.CanStoreAsync(organizationId.Value)
            : await _usageTrackingService.CanStoreAsync(userId);
        
        if (!canStore)
        {
            Log(node, NodeLogLevel.Warning, $"Storage limit reached: {reason}");
            return new NodeExecutionResult { Success = false, ErrorMessage = reason };
        }

        var blobClient = containerClient.GetBlobClient(filePath);
        
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

        // Check if file exists to calculate size delta for tracking
        long previousSize = 0;
        if (await blobClient.ExistsAsync())
        {
            var props = await blobClient.GetPropertiesAsync();
            previousSize = props.Value.ContentLength;
        }

        using var stream = new MemoryStream(bytes);
        await blobClient.UploadAsync(stream, overwrite: true);

        // Update usage tracking (delta = new size - old size)
        var sizeDelta = bytes.Length - previousSize;
        if (organizationId.HasValue)
        {
            await _orgUsageTrackingService.UpdateStorageAsync(organizationId.Value, blobStorageBytes: sizeDelta);
        }
        else
        {
            await _usageTrackingService.UpdateStorageAsync(userId, blobStorageBytes: sizeDelta);
            _usageTrackingService.InvalidateStorageLimitCache(userId);
        }

        Log(node, NodeLogLevel.Info, $"Wrote {bytes.Length} bytes to '{filePath}'");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "FilePath", filePath },
                { "BytesWritten", bytes.Length },
                { "Success", true }
            }
        };
    }

    private async Task<NodeExecutionResult> DeleteFileAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string filePath,
        string userId,
        Guid? organizationId)
    {
        if (string.IsNullOrEmpty(filePath))
            return new NodeExecutionResult { Success = false, ErrorMessage = "File path is required for Delete operation" };

        var blobClient = containerClient.GetBlobClient(filePath);
        
        // Get size before deleting for tracking
        long fileSize = 0;
        if (await blobClient.ExistsAsync())
        {
            var props = await blobClient.GetPropertiesAsync();
            fileSize = props.Value.ContentLength;
        }

        var deleted = await blobClient.DeleteIfExistsAsync();

        if (deleted)
        {
            // Update usage tracking (negative to reclaim space)
            if (organizationId.HasValue)
            {
                await _orgUsageTrackingService.UpdateStorageAsync(organizationId.Value, blobStorageBytes: -fileSize);
            }
            else
            {
                await _usageTrackingService.UpdateStorageAsync(userId, blobStorageBytes: -fileSize);
                _usageTrackingService.InvalidateStorageLimitCache(userId);
            }
            Log(node, NodeLogLevel.Info, $"Deleted file '{filePath}' ({fileSize} bytes freed)");
        }
        else
        {
            Log(node, NodeLogLevel.Warning, $"File '{filePath}' did not exist");
        }

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "FilePath", filePath },
                { "Deleted", deleted.Value },
                { "Success", true }
            }
        };
    }

    /// <summary>
    /// Deletes a folder and all its contents recursively.
    /// </summary>
    private async Task<NodeExecutionResult> DeleteFolderAsync(
        WorkflowNode node,
        BlobContainerClient containerClient,
        string folderPath,
        string userId,
        Guid? organizationId)
    {
        if (string.IsNullOrEmpty(folderPath))
            return new NodeExecutionResult { Success = false, ErrorMessage = "Folder path is required for DeleteFolder operation" };

        var prefix = folderPath.TrimEnd('/') + "/";
        var deletedCount = 0;
        long totalBytesFreed = 0;

        // Collect all blobs to delete
        var blobsToDelete = new List<(string Name, long Size)>();
        await foreach (var blobItem in containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, default))
        {
            blobsToDelete.Add((blobItem.Name, blobItem.Properties.ContentLength ?? 0));
        }

        // Delete all collected blobs
        foreach (var (blobName, size) in blobsToDelete)
        {
            var blobClient = containerClient.GetBlobClient(blobName);
            var deleted = await blobClient.DeleteIfExistsAsync();
            if (deleted)
            {
                deletedCount++;
                totalBytesFreed += size;
            }
        }

        // Update usage tracking (negative to reclaim space)
        if (totalBytesFreed > 0)
        {
            if (organizationId.HasValue)
            {
                await _orgUsageTrackingService.UpdateStorageAsync(organizationId.Value, blobStorageBytes: -totalBytesFreed);
            }
            else
            {
                await _usageTrackingService.UpdateStorageAsync(userId, blobStorageBytes: -totalBytesFreed);
                _usageTrackingService.InvalidateStorageLimitCache(userId);
            }
        }

        Log(node, NodeLogLevel.Info, $"Deleted folder '{folderPath}' with {deletedCount} files ({totalBytesFreed} bytes freed)");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "FolderPath", folderPath },
                { "DeletedCount", deletedCount },
                { "BytesFreed", totalBytesFreed },
                { "Success", true }
            }
        };
    }

    private async Task<NodeExecutionResult> CreateFolderAsync(
        WorkflowNode node, 
        BlobContainerClient containerClient, 
        string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
            return new NodeExecutionResult { Success = false, ErrorMessage = "Folder path is required for CreateFolder operation" };

        // Azure Blob doesn't have real folders - create a placeholder file
        var placeholderPath = folderPath.TrimEnd('/') + "/.folder";
        var blobClient = containerClient.GetBlobClient(placeholderPath);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));
        await blobClient.UploadAsync(stream, overwrite: true);

        Log(node, NodeLogLevel.Info, $"Created folder '{folderPath}'");

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "FolderName", folderPath },
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
    /// Checks for: sufficient length, valid base64 characters, no obvious text patterns.
    /// </summary>
    private static bool IsLikelyBase64(string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length < 100)
            return false;

        // Base64 content should be relatively long and have no whitespace/newlines
        // (or only at predictable intervals for formatted base64)
        var trimmed = content.Trim();
        
        // Check if it starts with common text patterns (JSON, XML, etc.)
        if (trimmed.StartsWith("{") || trimmed.StartsWith("[") || 
            trimmed.StartsWith("<") || trimmed.StartsWith("http"))
            return false;

        // Check character composition - base64 uses A-Z, a-z, 0-9, +, /, =
        var validChars = 0;
        var totalChars = Math.Min(trimmed.Length, 500); // Sample first 500 chars
        for (var i = 0; i < totalChars; i++)
        {
            var c = trimmed[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || 
                (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
                validChars++;
        }

        // If >95% of sampled chars are valid base64 chars, likely base64
        return (double)validChars / totalChars > 0.95;
    }

    /// <summary>
    /// Get the container name based on organization context.
    /// Organization context: org-{guid}
    /// Personal context: user-{userId}
    /// </summary>
    private static string GetContainerName(Dictionary<string, object?> inputData, string userId)
    {
        var orgId = GetOrganizationId(inputData);
        if (orgId.HasValue)
        {
            return $"org-{orgId.Value.ToString().ToLowerInvariant().Replace("-", "")}";
        }
        return $"user-{userId.ToLowerInvariant().Replace("-", "")}";
    }

    /// <summary>
    /// Extract organization ID from input data if present.
    /// </summary>
    private static Guid? GetOrganizationId(Dictionary<string, object?> inputData)
    {
        if (inputData.TryGetValue("_OrganizationId", out var orgIdObj))
        {
            if (orgIdObj is Guid orgId && orgId != Guid.Empty)
                return orgId;
            
            if (orgIdObj is string orgIdStr && Guid.TryParse(orgIdStr, out var parsedOrgId) && parsedOrgId != Guid.Empty)
                return parsedOrgId;
        }
        return null;
    }
}

/// <summary>
/// Configuration for S2G Storage operations.
/// </summary>
public class S2GStorageConfig
{
    /// <summary>Operation to perform: List, Read, Write, Edit, Delete, CreateFolder</summary>
    public string? Operation { get; set; } = "List";
    
    /// <summary>Path to the file for Read/Write/Delete operations (e.g., "documents/report.pdf")</summary>
    public string? FilePath { get; set; }
    
    /// <summary>Path to folder for List/CreateFolder operations (e.g., "documents/2024")</summary>
    public string? FolderPath { get; set; }
    
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
}
