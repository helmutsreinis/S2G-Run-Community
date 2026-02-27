using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// OneDrive trigger node that monitors a folder for new and/or modified files.
/// Uses time-based polling for change detection.
/// Supports recursive subfolder monitoring.
/// </summary>
public class OneDriveTriggerNode : BaseNodeExecutor
{
    private readonly OAuthService _oAuthService;
    
    // In-memory tracking of seen files per node
    private static readonly Dictionary<Guid, HashSet<string>> _seenFileIds = new();
    private static readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> _fileModifiedTimes = new();
    private static readonly object _lockObj = new();

    public OneDriveTriggerNode(NodeExecutionManager executionManager, OAuthService oAuthService)
        : base(executionManager)
    {
        _oAuthService = oAuthService;
    }

    public override string NodeType => "OneDriveTrigger";

    public override List<string> GetOutputParameters() => new() 
    { 
        "FileName", 
        "FilePath", 
        "FileId", 
        "DownloadUrl", 
        "FileSize", 
        "CreatedDateTime", 
        "LastModifiedDateTime",
        "MimeType",
        "TriggerType"
    };

    // Helper to add log via ExecutionManager (so UI gets updated)
    private void AddLog(Guid nodeId, NodeLogLevel level, string message, string? detail = null)
    {
        _executionManager.AddNodeLog(nodeId, level, message, detail);
    }

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node, 
        Dictionary<string, object?> inputData, 
        string userId)
    {
        var config = JsonSerializer.Deserialize<OneDriveTriggerConfig>(node.Configuration ?? "{}") ?? new();

        if (string.IsNullOrEmpty(config.ConnectionId))
        {
            AddLog(node.Id, NodeLogLevel.Error, "No Microsoft 365 connection selected");
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = "No Microsoft 365 connection selected" 
            };
        }

        if (!Guid.TryParse(config.ConnectionId, out var connectionId))
        {
            AddLog(node.Id, NodeLogLevel.Error, "Invalid connection ID");
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = "Invalid connection ID" 
            };
        }

        // Toggle running state
        if (_executionManager.IsRunning(node.Id))
        {
            _executionManager.StopNode(node.Id);
            
            // Clear tracking for this node
            lock (_lockObj)
            {
                _seenFileIds.Remove(node.Id);
                _fileModifiedTimes.Remove(node.Id);
            }
            
            AddLog(node.Id, NodeLogLevel.Info, $"Stopped OneDrive trigger for folder: {config.FolderPath}");
            node.Status = NodeStatus.Idle;
            return new NodeExecutionResult { Success = true };
        }

        // Get initial access token to validate connection
        AddLog(node.Id, NodeLogLevel.Info, "Validating connection...");
        var accessToken = await _oAuthService.GetValidAccessTokenAsync(connectionId);
        if (string.IsNullOrEmpty(accessToken))
        {
            AddLog(node.Id, NodeLogLevel.Error, "Failed to get valid access token. Connection may be expired.");
            return new NodeExecutionResult 
            { 
                Success = false, 
                ErrorMessage = "Failed to get valid access token. Connection may be expired." 
            };
        }
        AddLog(node.Id, NodeLogLevel.Info, "Connection validated successfully");

        var intervalMs = (config.PollIntervalSeconds > 0 ? config.PollIntervalSeconds : 60) * 1000;
        var folderId = config.FolderId ?? "root";
        var folderPath = config.FolderPath ?? "/";

        // Initialize tracking for this node
        lock (_lockObj)
        {
            _seenFileIds[node.Id] = new HashSet<string>();
            _fileModifiedTimes[node.Id] = new Dictionary<string, DateTimeOffset>();
        }

        var triggerMode = config.MonitorModified ? "new and modified files" : "new files only";
        var recursiveMode = config.IncludeSubfolders ? " (including subfolders)" : "";
        AddLog(node.Id, NodeLogLevel.Info, $"Starting OneDrive trigger for folder: {folderPath}{recursiveMode} (interval: {config.PollIntervalSeconds}s, monitoring: {triggerMode})");

        // Do initial scan to populate seen files (don't trigger on existing files)
        await InitialScan(node.Id, connectionId, folderId, config);

        // Start polling timer
        _executionManager.StartPollingTrigger(
            node.Id,
            intervalMs,
            async () => await PollForChanges(node.Id, connectionId, folderId, config));

        node.Status = NodeStatus.Running;
        AddLog(node.Id, NodeLogLevel.Info, "Trigger started successfully. Polling is now active.");
        return new NodeExecutionResult { Success = true };
    }

    private async Task InitialScan(
        Guid nodeId,
        Guid connectionId,
        string folderId,
        OneDriveTriggerConfig config)
    {
        try
        {
            var recursiveLabel = config.IncludeSubfolders ? " (recursive)" : "";
            AddLog(nodeId, NodeLogLevel.Info, $"Performing initial folder scan{recursiveLabel}...");
            
            var graphClient = await _oAuthService.GetGraphClientAsync(connectionId);
            if (graphClient == null)
            {
                AddLog(nodeId, NodeLogLevel.Error, "Failed to create Graph client during initial scan");
                return;
            }

            var drive = await graphClient.Me.Drive.GetAsync();
            if (drive?.Id == null)
            {
                AddLog(nodeId, NodeLogLevel.Error, "Could not get user's drive during initial scan");
                return;
            }

            string actualFolderId = folderId;
            if (folderId == "root")
            {
                var rootItem = await graphClient.Drives[drive.Id].Root.GetAsync();
                if (rootItem?.Id == null)
                {
                    AddLog(nodeId, NodeLogLevel.Error, "Could not get root folder during initial scan");
                    return;
                }
                actualFolderId = rootItem.Id;
            }

            // Scan folder (and subfolders if enabled)
            var allFiles = new List<DriveItem>();
            await ScanFolderRecursive(graphClient, drive.Id, actualFolderId, allFiles, config.IncludeSubfolders);
            
            lock (_lockObj)
            {
                foreach (var file in allFiles)
                {
                    _seenFileIds[nodeId].Add(file.Id!);
                    if (file.LastModifiedDateTime.HasValue)
                    {
                        _fileModifiedTimes[nodeId][file.Id!] = file.LastModifiedDateTime.Value;
                    }
                }
            }
            
            AddLog(nodeId, NodeLogLevel.Info, $"Initial scan complete. Found {allFiles.Count} existing files. These will be ignored.");
        }
        catch (Exception ex)
        {
            AddLog(nodeId, NodeLogLevel.Warning, $"Initial scan failed: {ex.Message}");
        }
    }

    private async Task ScanFolderRecursive(
        GraphServiceClient graphClient,
        string driveId,
        string folderId,
        List<DriveItem> allFiles,
        bool includeSubfolders)
    {
        var children = await graphClient.Drives[driveId].Items[folderId].Children.GetAsync();
        
        if (children?.Value == null) return;
        
        foreach (var item in children.Value)
        {
            if (item.Id == null) continue;
            
            if (item.File != null)
            {
                allFiles.Add(item);
            }
            else if (item.Folder != null && includeSubfolders)
            {
                // Recurse into subfolder
                await ScanFolderRecursive(graphClient, driveId, item.Id, allFiles, includeSubfolders);
            }
        }
    }

    private async Task PollForChanges(
        Guid nodeId, 
        Guid connectionId, 
        string folderId,
        OneDriveTriggerConfig config)
    {
        var pollStartTime = DateTime.UtcNow;
        var recursiveLabel = config.IncludeSubfolders ? " (recursive)" : "";
        AddLog(nodeId, NodeLogLevel.Info, $"Polling OneDrive folder for changes{recursiveLabel}...");
        
        try
        {
            // Check if tracking is still initialized
            lock (_lockObj)
            {
                if (!_seenFileIds.ContainsKey(nodeId))
                {
                    AddLog(nodeId, NodeLogLevel.Warning, "Tracking data not found - reinitializing");
                    _seenFileIds[nodeId] = new HashSet<string>();
                    _fileModifiedTimes[nodeId] = new Dictionary<string, DateTimeOffset>();
                }
            }
            
            var graphClient = await _oAuthService.GetGraphClientAsync(connectionId);
            if (graphClient == null)
            {
                AddLog(nodeId, NodeLogLevel.Error, "Failed to create Graph client - connection may be expired");
                return;
            }

            var drive = await graphClient.Me.Drive.GetAsync();
            if (drive?.Id == null)
            {
                AddLog(nodeId, NodeLogLevel.Error, "Could not get user's drive");
                return;
            }

            string actualFolderId = folderId;
            if (folderId == "root")
            {
                var rootItem = await graphClient.Drives[drive.Id].Root.GetAsync();
                if (rootItem?.Id == null)
                {
                    AddLog(nodeId, NodeLogLevel.Error, "Could not get root folder");
                    return;
                }
                actualFolderId = rootItem.Id;
            }

            // Get all files (recursively if enabled)
            var allFiles = new List<DriveItem>();
            await ScanFolderRecursive(graphClient, drive.Id, actualFolderId, allFiles, config.IncludeSubfolders);

            int newFileCount = 0;
            int modifiedFileCount = 0;

            foreach (var file in allFiles)
            {
                var fileId = file.Id!;
                bool isNewFile;
                bool isModified = false;
                
                lock (_lockObj)
                {
                    isNewFile = !_seenFileIds[nodeId].Contains(fileId);
                    
                    // Check for modifications if enabled
                    if (!isNewFile && config.MonitorModified && file.LastModifiedDateTime.HasValue)
                    {
                        if (_fileModifiedTimes[nodeId].TryGetValue(fileId, out var lastKnownModified))
                        {
                            isModified = file.LastModifiedDateTime.Value > lastKnownModified;
                        }
                    }
                }

                if (isNewFile || isModified)
                {
                    var triggerType = isNewFile ? "Created" : "Modified";
                    if (isNewFile) newFileCount++;
                    if (isModified) modifiedFileCount++;
                    
                    var fileData = new Dictionary<string, object?>
                    {
                        ["FileName"] = file.Name,
                        ["FilePath"] = file.ParentReference?.Path + "/" + file.Name,
                        ["FileId"] = file.Id,
                        ["DownloadUrl"] = file.AdditionalData?.TryGetValue("@microsoft.graph.downloadUrl", out var url) == true ? url?.ToString() : null,
                        ["FileSize"] = file.Size,
                        ["CreatedDateTime"] = file.CreatedDateTime?.ToString("o"),
                        ["LastModifiedDateTime"] = file.LastModifiedDateTime?.ToString("o"),
                        ["MimeType"] = file.File?.MimeType,
                        ["TriggerType"] = triggerType
                    };

                    AddLog(nodeId, NodeLogLevel.Info, $"🔔 File {triggerType.ToLower()}: {file.Name}", JsonSerializer.Serialize(fileData, new JsonSerializerOptions { WriteIndented = true }));

                    // Trigger downstream execution
                    _executionManager.TriggerNodeExecution(nodeId, fileData);
                    
                    // Update tracking
                    lock (_lockObj)
                    {
                        _seenFileIds[nodeId].Add(fileId);
                    }
                }
                
                // Always update modified time
                if (file.LastModifiedDateTime.HasValue)
                {
                    lock (_lockObj)
                    {
                        _fileModifiedTimes[nodeId][fileId] = file.LastModifiedDateTime.Value;
                    }
                }
            }

            var elapsed = (DateTime.UtcNow - pollStartTime).TotalMilliseconds;
            int seenCount;
            lock (_lockObj)
            {
                seenCount = _seenFileIds.TryGetValue(nodeId, out var seen) ? seen.Count : 0;
            }
            AddLog(nodeId, NodeLogLevel.Info, $"Poll complete in {elapsed:F0}ms. Files scanned: {allFiles.Count}, Tracked: {seenCount}, New: {newFileCount}, Modified: {modifiedFileCount}");
        }
        catch (Exception ex)
        {
            AddLog(nodeId, NodeLogLevel.Error, $"Error polling OneDrive: {ex.Message}");
        }
    }
}

public class OneDriveTriggerConfig
{
    public string? ConnectionId { get; set; }
    public string? FolderId { get; set; }
    public string? FolderPath { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    /// <summary>If true, also trigger on modified files (not just new files)</summary>
    public bool MonitorModified { get; set; } = false;
    /// <summary>If true, also monitor all subfolders recursively</summary>
    public bool IncludeSubfolders { get; set; } = false;
}
