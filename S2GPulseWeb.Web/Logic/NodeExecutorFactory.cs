using System;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic.Nodes;

namespace S2GPulseWeb.Web.Logic;

public class NodeExecutorFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly UserSecretService _secretService;
    private readonly NodeExecutionManager _executionManager;
    private readonly OAuthService _oAuthService;
    private readonly CacheStorageService _cacheStorageService;
    private readonly StorageTableService _storageTableService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly VectorDbService _vectorDbService;
    private readonly CustomNodeService _customNodeService;
    private readonly UsageTrackingService _usageTrackingService;
    private readonly OrganizationUsageTrackingService _orgUsageTrackingService;
    private readonly CopilotConnectorService _copilotConnectorService;
    private readonly KnowledgeBaseService _knowledgeBaseService;
    private readonly OpenClawWsSessionManager _openClawSessionManager;

    public NodeExecutorFactory(
        IHttpClientFactory httpClientFactory, 
        IConfiguration configuration, 
        UserSecretService secretService, 
        NodeExecutionManager executionManager,
        OAuthService oAuthService,
        CacheStorageService cacheStorageService,
        StorageTableService storageTableService,
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        VectorDbService vectorDbService,
        CustomNodeService customNodeService,
        UsageTrackingService usageTrackingService,
        OrganizationUsageTrackingService orgUsageTrackingService,
        CopilotConnectorService copilotConnectorService,
        KnowledgeBaseService knowledgeBaseService,
        OpenClawWsSessionManager openClawSessionManager)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _secretService = secretService;
        _executionManager = executionManager;
        _oAuthService = oAuthService;
        _cacheStorageService = cacheStorageService;
        _storageTableService = storageTableService;
        _dbContextFactory = dbContextFactory;
        _vectorDbService = vectorDbService;
        _customNodeService = customNodeService;
        _usageTrackingService = usageTrackingService;
        _orgUsageTrackingService = orgUsageTrackingService;
        _copilotConnectorService = copilotConnectorService;
        _knowledgeBaseService = knowledgeBaseService;
        _openClawSessionManager = openClawSessionManager;
    }

    public INodeExecutor CreateExecutor(string nodeType)
    {
        // Handle custom nodes with Custom_ prefix
        if (nodeType.StartsWith("Custom_"))
        {
            var definition = _customNodeService.GetDefinitionByKeySync(nodeType);
            if (definition == null)
            {
                // Run on thread pool to avoid blocking UI thread (prevents deadlock)
                definition = Task.Run(() => _customNodeService.GetDefinitionByKeyAsync(nodeType)).GetAwaiter().GetResult();
            }
            
            if (definition == null)
                throw new ArgumentException($"Custom node type not found: {nodeType}", nameof(nodeType));

            return new ScriptNodeExecutor(_executionManager, definition, _httpClientFactory);
        }

        return nodeType switch
        {
            "HttpRequest" => new HttpRequestNode(_httpClientFactory, _executionManager),
            "SqlServer" => new SqlNode(_executionManager),
            "Postgresql" => new PostgresNode(_executionManager),
            "MongoDB" => new MongoDbNode(_executionManager),
            "HttpListener" => new HttpListenerNode(_executionManager),
            "OpenAI" => new OpenAINode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "DeepSeek" => new DeepSeekNode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "DeepSeekAgent" => new DeepSeekAgentNode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "Anthropic" => new AnthropicNode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "Gemini" => new GeminiNode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "Mistral" => new MistralNode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "Groq" => new GroqNode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "AzureBlob" => new AzureBlobNode(_executionManager),
            "AzureQueueSend" => new AzureQueueSendNode(_executionManager),
            "AzureQueueMonitor" => new AzureQueueMonitorNode(_executionManager),
            "Queue" => new QueueNode(_executionManager),
            "Condition" => new ConditionNode(_executionManager),
            "HttpResponse" => new HttpResponseNode(_executionManager),
            "OneDriveTrigger" => new OneDriveTriggerNode(_executionManager, _oAuthService),
            "Cache" => new CacheNode(_executionManager, _cacheStorageService),
            "StorageTable" => new StorageTableNode(_executionManager, _storageTableService),
            "StorageClient" => new StorageClientNode(_executionManager, _storageTableService, _dbContextFactory),
            "Loop" => new LoopNode(_executionManager),
            "VectorDb" => new VectorDbNode(_executionManager, _vectorDbService),
            "VectorClient" => new VectorClientNode(_executionManager, _vectorDbService),
            "Scheduler" => new SchedulerNode(_executionManager),
            "Aggregator" => new AggregatorNode(_executionManager),
            "S2GStorage" => new S2GStorageNode(_executionManager, _usageTrackingService, _orgUsageTrackingService, _configuration),
            "FileDownload" => new FileDownloadNode(_httpClientFactory, _executionManager),
            "ExcelToJson" => new ExcelToJsonNode(_executionManager),
            "ConnectorToken" => new ConnectorTokenNode(_executionManager, _oAuthService, _dbContextFactory),
            "Orchestrator" => new OrchestratorNode(_executionManager),
            "Remote" => new RemoteNode(_executionManager),
            "RemoteCommand" => new RemoteCommandNode(_executionManager),
            "PdfOcr" => new PdfOcrNode(_httpClientFactory.CreateClient(), _secretService, _executionManager),
            "CopilotAgent" => new CopilotAgentNode(_copilotConnectorService, _executionManager),
            "OpenClaw" => new OpenClawNode(_executionManager, _openClawSessionManager),
            "Knowledge" => new KnowledgeNode(_executionManager, _knowledgeBaseService),
            _ => throw new ArgumentException($"Unsupported node type: {nodeType}", nameof(nodeType))
        };
    }
}

