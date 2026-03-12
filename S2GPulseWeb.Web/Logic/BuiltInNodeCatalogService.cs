using System.Text.Json;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Service for managing built-in node assignments to custom catalog categories.
/// Stores configuration in a JSON file to avoid database schema changes.
/// </summary>
public class BuiltInNodeCatalogService
{
    private readonly ILogger<BuiltInNodeCatalogService> _logger;
    private readonly string _configFilePath;
    private static readonly object _fileLock = new();
    
    // Cached data
    private static Dictionary<Guid, List<string>>? _categoryAssignments = null;
    private static Dictionary<string, string>? _iconOverrides = null;
    private static DateTime _lastLoadTime = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

    public BuiltInNodeCatalogService(ILogger<BuiltInNodeCatalogService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _configFilePath = Path.Combine(env.ContentRootPath, "builtin-node-catalog.json");
    }

    /// <summary>
    /// Gets the list of built-in node types assigned to a category.
    /// </summary>
    public List<string> GetAssignedNodes(Guid categoryId)
    {
        var assignments = LoadAssignments();
        return assignments.TryGetValue(categoryId, out var nodes) ? nodes : new List<string>();
    }

    /// <summary>
    /// Sets the list of built-in node types assigned to a category.
    /// </summary>
    public void SetAssignedNodes(Guid categoryId, List<string> nodeTypes)
    {
        lock (_fileLock)
        {
            var assignments = LoadAssignments(forceRefresh: true);
            
            if (nodeTypes.Count == 0)
            {
                assignments.Remove(categoryId);
            }
            else
            {
                assignments[categoryId] = nodeTypes;
            }
            
            SaveAssignments(assignments);
            _categoryAssignments = assignments;
            _lastLoadTime = DateTime.UtcNow;
        }
        
        _logger.LogInformation("Updated built-in node assignments for category {CategoryId}: {Count} nodes", 
            categoryId, nodeTypes.Count);
    }

    /// <summary>
    /// Removes all assignments for a category (call when deleting a category).
    /// </summary>
    public void RemoveCategoryAssignments(Guid categoryId)
    {
        SetAssignedNodes(categoryId, new List<string>());
    }

    /// <summary>
    /// Gets all category assignments (for bulk loading).
    /// </summary>
    public Dictionary<Guid, List<string>> GetAllAssignments()
    {
        return new Dictionary<Guid, List<string>>(LoadAssignments());
    }

    /// <summary>
    /// Gets custom SVG icon override for a built-in node type.
    /// </summary>
    public string? GetIconOverride(string nodeTypeKey)
    {
        LoadConfig();
        return _iconOverrides?.TryGetValue(nodeTypeKey, out var svg) == true ? svg : null;
    }

    /// <summary>
    /// Sets custom SVG icon override for a built-in node type.
    /// </summary>
    public void SetIconOverride(string nodeTypeKey, string? svgIcon)
    {
        lock (_fileLock)
        {
            LoadConfig(forceRefresh: true);
            _iconOverrides ??= new Dictionary<string, string>();
            
            if (string.IsNullOrWhiteSpace(svgIcon))
            {
                _iconOverrides.Remove(nodeTypeKey);
            }
            else
            {
                _iconOverrides[nodeTypeKey] = svgIcon;
            }
            
            SaveConfig();
        }
        
        _logger.LogInformation("Updated icon override for {NodeType}", nodeTypeKey);
    }

    /// <summary>
    /// Gets all icon overrides.
    /// </summary>
    public Dictionary<string, string> GetAllIconOverrides()
    {
        LoadConfig();
        return new Dictionary<string, string>(_iconOverrides ?? new());
    }

    private Dictionary<Guid, List<string>> LoadAssignments(bool forceRefresh = false)
    {
        LoadConfig(forceRefresh);
        return _categoryAssignments ?? new();
    }

    private void LoadConfig(bool forceRefresh = false)
    {
        // Use cache if available and not expired
        if (!forceRefresh && _categoryAssignments != null && 
            DateTime.UtcNow - _lastLoadTime < CacheExpiration)
        {
            return;
        }

        lock (_fileLock)
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<BuiltInNodeCatalogConfig>(json, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    _categoryAssignments = config?.CategoryAssignments ?? new();
                    _iconOverrides = config?.IconOverrides ?? new();
                }
                else
                {
                    _categoryAssignments = new Dictionary<Guid, List<string>>();
                    _iconOverrides = new Dictionary<string, string>();
                }
                
                _lastLoadTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading built-in node catalog config");
                _categoryAssignments = new Dictionary<Guid, List<string>>();
                _iconOverrides = new Dictionary<string, string>();
            }
        }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new BuiltInNodeCatalogConfig
            {
                CategoryAssignments = _categoryAssignments ?? new(),
                IconOverrides = _iconOverrides ?? new(),
                LastUpdated = DateTime.UtcNow
            };
            
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            
            File.WriteAllText(_configFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving built-in node catalog config");
            throw;
        }
    }

    private void SaveAssignments(Dictionary<Guid, List<string>> assignments)
    {
        _categoryAssignments = assignments;
        SaveConfig();
    }

    /// <summary>
    /// Gets all available built-in node types with their metadata.
    /// </summary>
    public static List<BuiltInNodeInfo> GetAllBuiltInNodeTypes()
    {
        return new List<BuiltInNodeInfo>
        {
            // HTTP
            new("HttpListener", "Listener", "🔊", "HTTP", 
                "HTTP webhook trigger that listens for incoming HTTP requests. Supports GET, POST, PUT, DELETE methods. Provides access to request headers, query parameters, and body content."),
            new("HttpRequest", "Request", "🌐", "HTTP",
                "Makes outbound HTTP/HTTPS requests to external APIs and services. Supports all HTTP methods, custom headers, authentication, and JSON/form-data payloads. Returns response body, headers, and status code."),
            new("HttpResponse", "Response", "📤", "HTTP",
                "Sends HTTP response back to the caller for Listener-triggered workflows. Configure status code, response headers, and body content. Required for completing webhook-based flows."),
            
            // AI
            new("OpenAI", "OpenAI", "🤖", "AI",
                "Integrates with OpenAI's GPT models for text generation, chat completion, and AI-powered transformations. Supports system prompts, temperature control, JSON mode, and token tracking for cost management."),
            new("DeepSeek", "DeepSeek", "🧠", "AI",
                "Connects to DeepSeek AI models for advanced reasoning and code generation tasks. Similar to OpenAI but optimized for analytical and programming scenarios."),
            new("DeepSeekAgent", "DeepSeek Agent", "🤖", "AI",
                "DeepSeek AI with function calling/tool use. Enables the AI to invoke connected workflow nodes as tools, executing them with AI-provided parameters and returning results."),
            new("CopilotAgent", "Copilot Agent", "🐙", "AI",
                "GitHub Copilot AI with function calling. Uses your existing Copilot Pro/Business subscription via OAuth device flow. Access GPT-4o, Claude 3.5 Sonnet, o1-preview and more through your Copilot Premium Requests."),
            new("OpenClaw", "OpenClaw Agent", "🦾", "AI",
                "Bridges S2G workflows with an OpenClaw Gateway AI agent. Supports bidirectional tool calling: the AI can invoke connected S2G nodes as tools and receive their results. Configure the gateway URL, auth token, and optional agent ID. Tool connections use 'tool:*' labelled edges. No iteration cap — runs until the model stops or the timeout is reached."),
            new("Anthropic", "Anthropic", "🅰️", "AI",
                "Integrates with Anthropic's Claude models for advanced AI reasoning, chat, image analysis, and tool use. Supports Claude Opus, Sonnet, and Haiku model families with system prompts and token cost tracking."),
            new("Gemini", "Gemini", "✨", "AI",
                "Connects to Google's Gemini AI models for text generation, reasoning, and multimodal tasks. Supports Gemini 2.5 Pro/Flash, 2.0 Flash, and 1.5 Pro/Flash with system instructions and token cost tracking."),
            new("Mistral", "Mistral", "🌀", "AI",
                "Integrates with Mistral AI's models for fast, efficient text generation. Supports Premier models (Mistral Large, Pixtral, Codestral) and General Purpose models (Mistral Small, Nemo, Mixtral) with conversation mode and cost tracking."),
            new("Groq", "Groq", "⚡", "AI",
                "Ultra-fast inference via Groq's LPU chips. Supports GPT-OSS models, Llama 4, Qwen3, and Llama 3.3/3.1 with conversation mode and cost tracking. Known for industry-leading inference speed."),
            new("LocalLlm", "Local LLM", "🖥️", "AI",
                "Connects to self-hosted OpenAI-compatible LLM servers (vLLM, Ollama, LM Studio, etc.). Configurable base URL, model, and thinking mode for Qwen3/reasoning models. Built-in diagnostics: test connection, list models, send test prompts."),
            new("LocalLlmAgent", "Local LLM Agent", "🖥️", "AI",
                "Self-hosted LLM with function calling/tool use. Enables the AI to invoke connected workflow nodes as tools via tool:* connections. Supports thinking mode, auto-discovery, and configurable max tool calls."),
            new("VectorDb", "Vector Store", "🧬", "AI",
                "Stores and indexes text embeddings for semantic search and RAG (Retrieval-Augmented Generation) applications. Supports chunking, metadata filtering, and similarity scoring."),
            new("VectorClient", "Vector Client", "🔍", "AI",
                "Queries vector databases for semantic similarity search. Retrieves relevant context based on natural language queries for AI-enhanced workflows."),
            new("PdfOcr", "PDF OCR (Mistral)", "📄", "AI",
                "Extracts text from PDF documents using Mistral OCR API. Supports scanned documents, images, and native PDFs. Returns plain text, structured Markdown with preserved formatting, and tables as JSON arrays. Requires Mistral API key. Pricing: $1/1000 pages."),
            
            // Database
            new("SqlServer", "SQL Server", "🗄️", "Database",
                "Executes queries against Microsoft SQL Server databases. Supports parameterized queries, stored procedures, transactions, and automatic column detection. Returns results as JSON arrays."),
            new("Postgresql", "PostgreSQL", "🐘", "Database",
                "Connects to PostgreSQL databases for reading and writing data. Full SQL support with parameterized queries, JSON operations, and connection pooling."),
            new("MongoDB", "MongoDB", "🍃", "Database",
                "Interfaces with MongoDB for document-based data operations. Supports find, insert, update, delete operations with full query syntax and aggregation pipelines."),
            
            // Cloud
            new("AzureStorage", "Azure Table", "📋", "Cloud",
                "Reads and writes to Azure Table Storage for structured NoSQL data. Supports partition/row key queries, entity batch operations, and visual filter builders."),
            new("AzureBlob", "Azure Blob", "🗂️", "Cloud",
                "Manages files in Azure Blob Storage containers. Operations: Read, Write, Edit, Delete, DeleteFolder (recursive), List. Supports metadata, content types, and SAS token generation."),
            new("AzureQueueSend", "Queue Send", "📨", "Cloud",
                "Sends messages to Azure Storage Queue. Configure connection string, queue name, and message content with placeholder support. Optionally set message TTL (time to live)."),
            new("AzureQueueMonitor", "Queue Monitor", "📧", "Cloud",
                "Monitors Azure Storage Queue for new messages. Triggers workflow execution when messages arrive. Automatically reads, extracts JSON properties recursively (if valid JSON), and removes messages from queue."),
            
            // Processing
            new("Queue", "Queue", "📬", "Processing",
                "In-memory message queue for buffering and rate-limiting workflow executions. Accumulates upstream data and releases on capacity or timeout triggers."),
            new("Condition", "Condition", "❓", "Processing",
                "Branches workflow execution based on logical conditions. Evaluates JavaScript expressions to route data through different connection paths using tags."),
            new("Loop", "Loop", "🔁", "Processing",
                "Iterates over arrays or collections, executing downstream nodes for each item. Provides current item, index, and loop context for nested processing."),
            new("Aggregator", "Aggregator", "📥", "Processing",
                "Collects and combines data from multiple upstream executions. Waits for all inputs to arrive before releasing the aggregated result downstream."),
            
            // Storage
            new("Cache", "Cache", "💾", "Storage",
                "High-performance in-memory key-value store for workflow data. Supports TTL expiration, atomic operations, and acts as a barrier for coordinating parallel executions."),
            new("StorageTable", "Table", "🗃️", "Storage",
                "Persistent local table storage for workflow data. Store, query, and manage structured data with automatic schema detection and filtering capabilities."),
            new("StorageClient", "Client", "📊", "Storage",
                "Client interface for reading from Storage Tables. Optimized for lookup operations with support for multiple output formats."),
            new("FileDownload", "File Download", "⬇️", "Storage",
                "Downloads files from URLs with support for authentication headers. Outputs base64-encoded content that can be saved to S2G Storage, Azure Blob, or processed downstream."),
            new("ExcelToJson", "Excel to JSON", "📊", "Processing",
                "Converts Excel files (.xlsx, .xls) to JSON arrays. Automatically detects table boundaries and infers column data types (String, Number, Boolean, Date). Supports multiple sheets with separate output properties. Input from FileDownload, S2GStorage, or AzureBlob nodes."),
            new("S2GStorage", "My Storage", "📁", "Storage",
                "Personal file storage for your workflows. Operations: List, Read, Write, Edit, Delete, DeleteFolder (recursive), CreateFolder. Supports folders, drag-and-drop upload, and integrates with your storage quota."),
            
            // Triggers
            new("Scheduler", "Scheduler", "⏰", "Triggers",
                "Cron-based scheduled trigger for timed workflow executions. Supports standard cron expressions, timezone configuration, and provides next/last run timestamps."),
            new("OneDriveTrigger", "OneDrive Monitor", "📂", "Triggers",
                "Monitors OneDrive folders for file changes. Triggers workflow execution when files are created, modified, or deleted. Provides file metadata and download links."),
            
            // Integrations
            new("ConnectorToken", "Connector Token", "🔑", "Integrations",
                "Extracts a valid OAuth access token from an active connector. Use this token in custom HTTP requests for authenticated API calls. Automatically refreshes expired tokens."),
            new("Remote", "Remote Machine", "🖥️", "Integrations",
                "Execute commands on remote Linux/Windows machines. Queue commands with expiration, receive output and system metadata. Clients poll via HttpListener proxy for pending commands and submit execution results."),
            new("RemoteCommand", "Remote Command", "📡", "Integrations",
                "Execute commands synchronously on multiple Remote Machine nodes. Connect to Remote nodes with auto-labeled run:rm-* connections, configure command and timeout, and receive aggregated JSON results with execution status from all targets."),
            
            // Scripting
            new("PowerShell", "PowerShell", "🐚", "Scripting",
                "Executes PowerShell scripts for system automation tasks. Access to full PowerShell ecosystem, environment variables, and file system operations. Returns script output as structured data."),
            
            // Orchestration
            new("Orchestrator", "Orchestrator", "🎭", "AI",
                "Coordinates multi-agent AI workflows with iterative refinement. Assign agents to roles (e.g., DB Expert, QA Validator, Reporter), connect tool chains (SQL, HTTP, etc.), and define success criteria. Supports fan-out parallel tool execution and expression or AI-based evaluation."),

            // Knowledge
            new("Knowledge", "Knowledge Base", "📚", "Knowledge",
                "Read and write from the Knowledge Base. Supports 11 operations: Search, GetEntity, AddEntity, UpdateEntity, DeleteEntity, AddRelation, RemoveRelation, GetRelations, GetGraph, ListEntities, ListTypes. Works with personal and organization-scoped knowledge stores."),
        };
    }
}

/// <summary>
/// Configuration model for built-in node catalog assignments.
/// </summary>
public class BuiltInNodeCatalogConfig
{
    public Dictionary<Guid, List<string>> CategoryAssignments { get; set; } = new();
    /// <summary>Key: NodeTypeKey, Value: Custom SVG markup</summary>
    public Dictionary<string, string> IconOverrides { get; set; } = new();
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Metadata for a built-in node type.
/// </summary>
public record BuiltInNodeInfo(string NodeTypeKey, string DisplayName, string Icon, string Category, string Description = "");

