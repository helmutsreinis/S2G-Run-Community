using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;
using S2GPulseWeb.Web.Logic.Nodes;
using System.Text.Json;
using System.Threading;

namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

public partial class Designer : IDisposable
{
    // Injected services
    [Inject] private WorkflowAssistantService AssistantService { get; set; } = default!;
    [Inject] private NodeExecutorFactory ExecutorFactory { get; set; } = default!;
    [Inject] private UsageTrackingService UsageTrackingService { get; set; } = default!;
    [Inject] private CustomNodeService CustomNodeService { get; set; } = default!;
    [Inject] private BuiltInNodeCatalogService BuiltInNodeCatalogService { get; set; } = default!;
    [Inject] private NodeKnowledgeService NodeKnowledgeService { get; set; } = default!;
    [Inject] private UserSecretService SecretService { get; set; } = default!;
    [Inject] private CopilotConnectorService CopilotService { get; set; } = default!;
    [Inject] private OrganizationService OrganizationService { get; set; } = default!;
    [Inject] private OrganizationUsageTrackingService OrgUsageTrackingService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>Scheme + host of this S2G server, used to build trigger URLs in node editors.</summary>
    private string ServerBaseUrl => new Uri(NavigationManager.Uri).GetLeftPart(UriPartial.Authority);

    // State - User and Workflow
    private string? currentUserId;
    private Guid? currentWorkflowId;
    private string currentWorkflowName = "New Workflow";
    private bool currentWorkflowIsActive = false; // Auto-start on app startup
    private Guid? currentWorkflowOrganizationId; // Organization context (null = personal)
    private bool canDeleteWorkflow = true; // Contributors cannot delete org workflows
    private Guid? activeOrganizationId; // Currently active organization from user preferences
    private List<Data.Workflow> userWorkflows = new();
    private bool showWorkflowList = false;
    private bool showCacheViewer = false;
    private bool showStorageTableViewer = false;
    private bool showVectorDbViewer = false;
    private bool showBlobViewer = false;
    private bool showS2GStorageViewer = false;
    private Guid? viewingStorageTableNodeId;
    private Guid? viewingCacheNodeId;
    private Guid? viewingVectorDbNodeId;
    private Guid? viewingBlobNodeId;
    private Guid? viewingS2GStorageNodeId;
    private bool showRemoteMachineMonitor = false;
    private Guid? viewingRemoteNodeId;
    private string? viewingRemoteClientId;
    
    // Workflow state tracking
    private bool hasUnsavedChanges = false;
    private string? workflowNotificationMessage = null;
    private string workflowNotificationType = "warning"; // warning, danger, info, success
    
    // Tier limits for current user
    private TierLimits? currentTierLimits;
    
    // Workflow deletion state
    private bool showDeleteConfirmation = false;
    private bool isDeleting = false;
    private WorkflowDeletionInfo? deletionInfo;
    
    // Node deletion state
    private bool showNodeDeleteConfirmation = false;
    private CanvasNode? nodeToDelete = null;

    // Canvas state
    private ElementReference canvasRef;
    private ElementReference importFileInput;
    private List<CanvasNode> canvasNodes = new();
    private List<NodeConnection> connections = new();
    private string? draggedNodeType;
    private Guid? selectedNodeId;
    private CanvasNode? draggingNode;
    private CanvasNode? resizingNode;
    private double dragStartX, dragStartY;
    private double nodeStartX, nodeStartY;
    private double nodeStartWidth, nodeStartHeight;
    
    // Connection drawing
    private bool isConnecting = false;
    private CanvasNode? connectingFromNode;
    private double connectingEndX, connectingEndY;
    private bool wasDragged = false;
    private bool wasResizing = false;
    
    // Connection context menu
    private bool showConnectionContextMenu = false;
    private NodeConnection? contextMenuConnection;
    private double connectionContextMenuX, connectionContextMenuY;
    private string connectionLabelInput = "";
    
    // Surface field context menu state
    private bool showSurfaceFieldMenu = false;
    private bool showAddSurfaceFieldMenu = false;
    private CanvasNode? surfaceFieldMenuNode;
    private string? surfaceFieldToRemove;
    private double surfaceFieldMenuX, surfaceFieldMenuY;
    private string surfaceFieldSearchText = "";
    private string surfaceFieldEditText = "";
    
    // Workflow running state - computed from service
    private bool isWorkflowRunning => currentWorkflowId.HasValue && WorkflowExecutionService.IsRunning(currentWorkflowId.Value);
    
    // Scheduler countdown refresh timer
    private Timer? countdownRefreshTimer;
    
    // Canvas tracking
    private double canvasOffsetX, canvasOffsetY;
    private double canvasWidth = DesignerConstants.DefaultCanvasWidth;
    private double canvasHeight = DesignerConstants.DefaultCanvasHeight;
    
    // Pan and zoom
    private double panX = 0;
    private double panY = 0;
    private double zoomLevel = 1.0;
    private bool isPanning = false;
    private double panStartX, panStartY;
    private double panStartPanX, panStartPanY;
    
    // UI state
    private bool isPaletteCollapsed = false;
    private bool isAiPanelCollapsed = true;
    private string aiInputMessage = "";
    private bool isAiThinking = false;
    private ElementReference aiMessagesRef;
    
    // AI Builder - Provider and Mode
    private string selectedAiProvider = "OpenAI";
    private string selectedAiModel = "gpt-4o";
    private List<string> availableModels = new() { "gpt-4o" };
    private string aiChatMode = "Ask"; // Ask, Build, Edit
    private string aiTemperatureMode = "Focused"; // Focused or Creative
    private List<AiProviderInfo> aiProviders = new();
    private Guid? copilotConnectionId = null; // Designated Copilot connection for AI Builder
    private string? copilotConnectionEmail = null; // Display name for Copilot connection
    
    // AI Builder - Panel Position (draggable)
    private double aiPanelX = 0;
    private double aiPanelY = 160;
    private bool isDraggingAiPanel = false;
    private double aiPanelDragStartX;
    private double aiPanelDragStartY;
    
    // AI Builder - Node Autocomplete
    private bool showNodeAutocomplete = false;
    private List<string> nodeAutocompleteMatches = new();
    private int selectedAutocompleteIndex = 0;
    
    // AI Builder - Context-selected nodes
    private List<CanvasNode> aiContextNodes = new();
    
    private bool showContextMenu = false;
    private double contextMenuX;
    private double contextMenuY;
    private CanvasNode? contextMenuNode;
    private CanvasNode? editingNode;
    private string nodeSearchText = "";
    private string activeModalTab = "properties";
    private NodeLogEntry? selectedLogEntry;
    private HashSet<Guid> executingNodeIds = new();
    private HashSet<Guid> activeConnectionIds = new();
    private bool showAllPlaceholders = false;
    private string selectedToolbarCategory = "HTTP";

    // Custom nodes from Admin Node Designer
    private List<CustomNodeDefinition> customNodeDefinitions = new();
    private List<CustomNodeCategory> customNodeCategories = new();
    private List<CustomNodeCatalogItem> customNodeCatalogItems = new();  // Lightweight catalog (no LEFT JOINs)
    
    // Workflow loading state
    private bool isLoadingWorkflowNodes = false;
    private string workflowLoadingStatus = "";
    
    // Workflow saving state
    private bool isSavingWorkflow = false;
    private string workflowSavingStatus = "";

    private bool overlayMouseDown = false;
    private bool isCostPanelCollapsed = true;
    private HashSet<string> expandedCategories = new() { "HTTP", "AI", "Data", "Files", "Integration", "Automation" };
    private ConnectionSide? connectingFromSide;
    
    // Minimap state
    private bool isMinimapCollapsed = true;
    private bool isMinimapDragging = false;
    private const double MaxCanvasWidth = 3000;
    private const double MaxCanvasHeight = 3000;
    private const double MinimapWidth = 180;
    private const double MinimapHeight = 140;
    
    // Custom Node Panel state
    private bool isCustomNodePanelCollapsed = true;
    private string customNodeSearchText = "";
    private HashSet<Guid> expandedCatalogCategories = new();
    private HashSet<Guid> loadingCategories = new(); // Track which categories are currently loading
    private bool isSearchLoading = false; // Track search loading state

    // Helper instance
    private PlaceholderHelper? _placeholderHelper;
    private PlaceholderHelper PlaceholderHelperInstance => _placeholderHelper ??= new PlaceholderHelper(CacheStorageService, CustomNodeService);

    // Cost Calculation
    private double TotalWorkflowCost => CalculateTotalWorkflowCost();

    #region UI Toggles

    private bool IsCategoryExpanded(string category) => expandedCategories.Contains(category);
    
    private void ToggleCategory(string category)
    {
        if (expandedCategories.Contains(category))
            expandedCategories.Remove(category);
        else
            expandedCategories.Add(category);
    }

    private bool IsNodeVisible(string name, string type)
    {
        if (string.IsNullOrWhiteSpace(nodeSearchText)) return true;
        return name.Contains(nodeSearchText, StringComparison.OrdinalIgnoreCase) || 
               type.Contains(nodeSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void TogglePalette() => isPaletteCollapsed = !isPaletteCollapsed;
    private void ToggleAiPanel() => isAiPanelCollapsed = !isAiPanelCollapsed;
    private void ToggleCostPanel() => isCostPanelCollapsed = !isCostPanelCollapsed;
    private void ToggleCustomNodePanel() => isCustomNodePanelCollapsed = !isCustomNodePanelCollapsed;

    #endregion

    #region Lifecycle

    protected override async Task OnInitializedAsync()
    {
        // Register event handlers for execution updates
        ExecutionManager.OnNodeLogAdded += HandleNodeLogAdded;
        
        // Register animation event handlers from WorkflowExecutionService
        WorkflowExecutionService.OnConnectionTraversalStarted += HandleConnectionTraversalStarted;
        WorkflowExecutionService.OnConnectionTraversalEnded += HandleConnectionTraversalEnded;
        WorkflowExecutionService.OnNodeExecutionStarted += HandleNodeExecutionStarted;
        WorkflowExecutionService.OnNodeExecutionCompleted += HandleNodeExecutionCompleted;
        WorkflowExecutionService.OnNodeOutputDataUpdated += HandleNodeOutputDataUpdated;
        
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        // Use claims to get user ID directly - avoids DbContext concurrency issues
        currentUserId = authState.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (!string.IsNullOrEmpty(currentUserId))
        {
            // Load user's workflows for the list (filtered by active organization context)
            activeOrganizationId = await OrganizationService.GetActiveOrganizationIdAsync(currentUserId);
            userWorkflows = await WorkflowService.GetUserWorkflowsAsync(currentUserId, activeOrganizationId);
            
            // Load tier limits for the user
            currentTierLimits = await UsageTrackingService.GetUserTierLimitsAsync(currentUserId);

            // Load last opened workflow - but only if it belongs to the current context
            var lastWorkflowId = await PreferenceService.GetLastWorkflowAsync(currentUserId);
            if (lastWorkflowId.HasValue)
            {
                var workflow = await WorkflowService.GetWorkflowAsync(lastWorkflowId.Value);
                // Only load if workflow matches current org context:
                // - In personal context (activeOrganizationId == null): load only personal workflows (workflow.OrganizationId == null)
                // - In org context: load only workflows from that org
                if (workflow != null && workflow.OrganizationId == activeOrganizationId)
                {
                    await LoadWorkflowFromData(workflow);
                }
            }
        }
        // Load categories and lightweight catalog (no LEFT JOINs)
        customNodeCategories = await CustomNodeService.GetCategoriesAsync();
        customNodeCatalogItems = await CustomNodeService.GetCatalogItemsAsync();
        
        // Load AI providers with API key status
        await LoadAiProvidersAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await UpdateCanvasOffset();
            
            // Subscribe to node configuration updates for real-time refresh
            ExecutionManager.OnNodeConfigurationUpdated += HandleNodeConfigurationUpdated;
            
            // Start countdown refresh timer (updates every second for scheduler nodes)
            countdownRefreshTimer = new Timer(
                callback: _ => InvokeAsync(StateHasChanged),
                state: null,
                dueTime: 1000,
                period: 1000);
        }
    }

    public void Dispose()
    {
        // Stop countdown refresh timer
        countdownRefreshTimer?.Dispose();
        countdownRefreshTimer = null;
        
        ExecutionManager.OnNodeLogAdded -= HandleNodeLogAdded;
        ExecutionManager.OnNodeConfigurationUpdated -= HandleNodeConfigurationUpdated;
        
        // Unsubscribe from animation events
        WorkflowExecutionService.OnConnectionTraversalStarted -= HandleConnectionTraversalStarted;
        WorkflowExecutionService.OnConnectionTraversalEnded -= HandleConnectionTraversalEnded;
        WorkflowExecutionService.OnNodeExecutionStarted -= HandleNodeExecutionStarted;
        WorkflowExecutionService.OnNodeExecutionCompleted -= HandleNodeExecutionCompleted;
        WorkflowExecutionService.OnNodeOutputDataUpdated -= HandleNodeOutputDataUpdated;
    }

    #endregion
}
