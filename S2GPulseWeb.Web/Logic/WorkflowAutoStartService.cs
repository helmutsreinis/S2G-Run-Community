using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Components.Pages.Workflow.Designer;
using System.Text.Json;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Background service that auto-starts workflows marked as active on application startup.
/// This ensures that listener-based workflows (HttpListener, Timer, etc.) resume after deployments.
/// </summary>
public class WorkflowAutoStartService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkflowExecutionService _executionService;
    private readonly ILogger<WorkflowAutoStartService> _logger;

    public WorkflowAutoStartService(
        IServiceScopeFactory scopeFactory,
        WorkflowExecutionService executionService,
        ILogger<WorkflowAutoStartService> logger)
    {
        _scopeFactory = scopeFactory;
        _executionService = executionService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WorkflowAutoStartService: Starting auto-start check...");
        
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            // Get all workflows marked as active (auto-start enabled)
            var activeWorkflows = await context.Workflows
                .Include(w => w.Nodes)
                    .ThenInclude(n => n.OutgoingConnections)
                .Include(w => w.Nodes)
                    .ThenInclude(n => n.IncomingConnections)
                .Where(w => w.IsActive)
                .ToListAsync(cancellationToken);
            
            if (!activeWorkflows.Any())
            {
                _logger.LogInformation("WorkflowAutoStartService: No active workflows to auto-start.");
                return;
            }
            
            _logger.LogInformation("WorkflowAutoStartService: Found {Count} active workflow(s) to auto-start.", activeWorkflows.Count);
            
            foreach (var workflow in activeWorkflows)
            {
                if (cancellationToken.IsCancellationRequested) break;
                
                try
                {
                    // Skip if already running
                    if (_executionService.IsRunning(workflow.Id))
                    {
                        _logger.LogDebug("WorkflowAutoStartService: Workflow '{Name}' is already running, skipping.", workflow.Name);
                        continue;
                    }
                    
                    // Prepare node data for StartWorkflowAsync
                    var nodes = workflow.Nodes.Select(n => (
                        Id: n.Id,
                        NodeType: n.NodeType,
                        Name: n.Name,
                        Configuration: n.Configuration,
                        IsTrigger: n.IsTrigger,
                        LoggingSettings: ParseLoggingSettings(n.LoggingSettingsJson)
                    )).ToList();
                    
                    var connections = workflow.Nodes
                        .SelectMany(n => n.OutgoingConnections)
                        .Select(c => (
                            Id: c.Id,
                            SourceId: c.SourceNodeId,
                            TargetId: c.TargetNodeId,
                            Label: c.Label
                        )).ToList();
                    
                    var (success, error) = await _executionService.StartWorkflowAsync(
                        workflow.Id,
                        workflow.OwnerId,
                        workflow.Name,
                        nodes,
                        connections,
                        workflow.OrganizationId
                    );
                    
                    if (success)
                    {
                        _logger.LogInformation("WorkflowAutoStartService: Auto-started workflow '{Name}' (ID: {Id})", 
                            workflow.Name, workflow.Id);
                    }
                    else
                    {
                        _logger.LogWarning("WorkflowAutoStartService: Failed to auto-start workflow '{Name}': {Error}", 
                            workflow.Name, error);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WorkflowAutoStartService: Error auto-starting workflow '{Name}' (ID: {Id})", 
                        workflow.Name, workflow.Id);
                }
            }
            
            _logger.LogInformation("WorkflowAutoStartService: Auto-start check completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WorkflowAutoStartService: Failed to run auto-start check.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WorkflowAutoStartService: Stopping...");
        return Task.CompletedTask;
    }
    
    private static NodeLoggingSettings ParseLoggingSettings(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new NodeLoggingSettings();
        
        try
        {
            return JsonSerializer.Deserialize<NodeLoggingSettings>(json) ?? new NodeLoggingSettings();
        }
        catch
        {
            return new NodeLoggingSettings();
        }
    }
}
