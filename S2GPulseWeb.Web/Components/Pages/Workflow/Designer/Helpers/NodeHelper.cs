namespace S2GPulseWeb.Web.Components.Pages.Workflow.Designer;

/// <summary>
/// Helper class for node-related operations: icons, default names, and configurations.
/// </summary>
public static class NodeHelper
{
    /// <summary>
    /// Gets the emoji icon for a node type.
    /// For custom nodes (Custom_ prefix), returns fallback - actual icon should be fetched from definition.
    /// </summary>
    public static string GetNodeIcon(string nodeType)
    {
        // Custom nodes use SVG icons from their definition
        if (nodeType.StartsWith("Custom_"))
            return "⚙️"; // Fallback emoji, actual rendering uses SVG from definition
            
        return nodeType switch
        {
            "HttpListener" => "🔊",
            "HttpRequest" => "🌐",
            "SqlServer" => "🗄️",
            "Postgresql" => "🐘",
            "MongoDB" => "🍃",
            "AzureStorage" => "📋",
            "AzureBlob" => "🗂️",
            "AzureQueueSend" => "📨",
            "AzureQueueMonitor" => "📧",
            "HttpResponse" => "📤",
            "OpenAI" => "🤖",
            "DeepSeek" => "🧠",
            "DeepSeekAgent" => "🤖",
            "CopilotAgent" => "🐙",
            "OpenClaw" => "🦾",
            "Anthropic" => "🅰️",
            "Gemini" => "✨",
            "Mistral" => "🌀",
            "Groq" => "⚡",
            "LocalLlm" => "🖥️",
            "LocalLlmAgent" => "🖥️",
            "FileMove" => "📦",
            "FileCopy" => "📋",
            "FileDelete" => "🗑️",
            "FileExists" => "🔍",
            "CreateFolder" => "📂",
            "PowerShell" => "🐚",
            "Queue" => "📬",
            "OneDriveTrigger" => "📂",
            "Condition" => "❓",
            "Cache" => "💾",
            "StorageTable" => "🗃️",
            "StorageClient" => "📊",
            "Loop" => "🔁",
            "VectorDb" => "🧬",
            "VectorClient" => "🔍",
            "Scheduler" => "⏰",
            "Aggregator" => "📥",
            "S2GStorage" => "📁",
            "FileDownload" => "⬇️",
            "ExcelToJson" => "📊",
            "ConnectorToken" => "🔑",
            "Orchestrator" => "🎭",
            "Remote" => "🖥️",
            "RemoteCommand" => "📡",
            "PdfOcr" => "📄",
            "Knowledge" => "📚",
            _ => "⚙️"
        };
    }

    /// <summary>
    /// Checks if a node type is a custom (user-defined) node.
    /// </summary>
    public static bool IsCustomNode(string nodeType)
    {
        return nodeType.StartsWith("Custom_");
    }

    /// <summary>
    /// Gets the display icon for a node, respecting any custom icon override.
    /// </summary>
    public static string GetDisplayIcon(string nodeType, string? iconOverride)
    {
        return string.IsNullOrEmpty(iconOverride) ? GetNodeIcon(nodeType) : iconOverride;
    }

    /// <summary>
    /// Gets all available icons organized by category for the icon picker.
    /// </summary>
    public static Dictionary<string, List<string>> GetAllAvailableIcons()
    {
        return new Dictionary<string, List<string>>
        {
            ["Common"] = new() { "⚙️", "🔧", "🛠️", "📌", "🎯", "💡", "⭐", "🔥", "✨", "💫", "🚀", "⚡", "🔔", "🏷️", "📍", "🔮", "💎", "🎪", "🎭", "🎨", "🎬", "🎤", "🎧", "🎵", "🎶", "🎹", "🎸", "🎺", "🎻", "🎲" },
            ["Data"] = new() { "📊", "📈", "📉", "🗃️", "🗄️", "💾", "💿", "📁", "📂", "🗂️", "📋", "📑", "📝", "✏️", "📎", "📐", "📏", "🧮", "📒", "📓", "📔", "📕", "📖", "📗", "📘", "📙", "📚", "🗒️", "🗓️", "📅" },
            ["Communication"] = new() { "📬", "📮", "📧", "💬", "💭", "🗨️", "📢", "📣", "🔊", "🔔", "📞", "☎️", "📱", "💻", "🖥️", "📟", "📠", "📡", "🔗", "✉️", "📨", "📩", "📤", "📥", "📦", "🏤", "🏣", "📪", "📫", "📭" },
            ["AI & Tech"] = new() { "🤖", "🧠", "🧬", "🔬", "🧪", "💡", "🌐", "🔗", "⛓️", "🔐", "🔑", "🛡️", "🎮", "🕹️", "📡", "🖨️", "⌨️", "🖱️", "🖲️", "💽", "💿", "📀", "🔌", "🔋", "💻", "🖥️", "🖳", "📺", "📻", "🎛️" },
            ["Arrows"] = new() { "➡️", "⬅️", "⬆️", "⬇️", "↗️", "↘️", "↙️", "↖️", "🔄", "🔁", "🔃", "🔀", "↩️", "↪️", "⤴️", "⤵️", "🔙", "🔚", "🔛", "🔜", "🔝", "↔️", "↕️", "🔲", "🔳", "▶️", "◀️", "🔼", "🔽", "⏩" },
            ["Status"] = new() { "✅", "❌", "⚠️", "❓", "❗", "💯", "🏁", "🎉", "🎊", "👍", "👎", "👁️", "🕐", "⏰", "⏳", "⌛", "🔴", "🟠", "🟡", "🟢", "🔵", "🟣", "⚪", "⚫", "🟤", "✔️", "☑️", "🆗", "🆕", "🆙" },
            ["Nature"] = new() { "🌍", "🌎", "🌏", "☀️", "🌙", "⭐", "🌈", "☁️", "🔥", "💧", "🌊", "⚡", "❄️", "🍃", "🌱", "🌲", "🌳", "🌴", "🌵", "🌷", "🌸", "🌹", "🌺", "🌻", "🌼", "🌿", "☘️", "🍀", "🍁", "🍂" },
            ["Objects"] = new() { "🎁", "🎀", "🏆", "🥇", "🎖️", "📦", "🧰", "🔨", "⛏️", "🔩", "🧲", "💎", "💰", "💵", "🏦", "🔦", "🕯️", "💊", "💉", "🩺", "🔭", "🔍", "🔎", "📿", "🧿", "🗝️", "🪙", "💳", "🧾", "🎫" },
            ["Animals"] = new() { "🐶", "🐱", "🐭", "🐹", "🐰", "🦊", "🐻", "🐼", "🐨", "🐯", "🦁", "🐮", "🐷", "🐸", "🐵", "🐔", "🐧", "🐦", "🐤", "🦆", "🦅", "🦉", "🦇", "🐺", "🐗", "🐴", "🦄", "🐝", "🐛", "🦋" },
            ["Food"] = new() { "🍎", "🍐", "🍊", "🍋", "🍌", "🍉", "🍇", "🍓", "🍒", "🍑", "🥭", "🍍", "🥥", "🥝", "🍅", "🥑", "🥦", "🥬", "🥒", "🌶️", "🌽", "🥕", "🧄", "🧅", "🥔", "🍞", "🥐", "🥖", "🧁", "🍰" },
            ["Transport"] = new() { "🚗", "🚕", "🚙", "🚌", "🚎", "🏎️", "🚓", "🚑", "🚒", "🚐", "🚚", "🚛", "🚜", "🛵", "🏍️", "🚲", "🛴", "🚁", "✈️", "🛩️", "🚀", "🛸", "🚢", "⛵", "🚤", "⚓", "🗼", "🏰", "🎡", "🎢" },
            ["Symbols"] = new() { "❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "🤎", "💔", "❣️", "💕", "💞", "💓", "💗", "💖", "💘", "💝", "☮️", "✝️", "☪️", "🕉️", "☯️", "✡️", "🔯", "♈", "♉", "♊", "♋", "♌" }
        };
    }

    /// <summary>
    /// Gets the default display name for a node type.
    /// For custom nodes, this returns a generic name - actual name should come from definition.
    /// </summary>
    public static string GetDefaultNodeName(string nodeType)
    {
        // Custom nodes get their display name from the definition
        if (nodeType.StartsWith("Custom_"))
            return nodeType.Replace("Custom_", "").Replace("_", " ");
            
        return nodeType switch
        {
            "HttpListener" => "Listener",
            "HttpResponse" => "Response",
            "HttpRequest" => "Request",
            "SqlServer" => "SQL Server",
            "Postgresql" => "PostgreSQL",
            "MongoDB" => "MongoDB",
            "AzureStorage" => "Azure Table",
            "AzureBlob" => "Azure Blob",
            "AzureQueueSend" => "Queue Send",
            "AzureQueueMonitor" => "Queue Monitor",
            "OpenAI" => "OpenAI",
            "DeepSeek" => "DeepSeek",
            "DeepSeekAgent" => "DeepSeek Agent",
            "CopilotAgent" => "Copilot Agent",
            "OpenClaw" => "OpenClaw Agent",
            "Anthropic" => "Anthropic",
            "Gemini" => "Gemini",
            "Mistral" => "Mistral",
            "Groq" => "Groq",
            "LocalLlm" => "Local LLM",
            "LocalLlmAgent" => "Local LLM Agent",
            "Queue" => "Queue",
            "PowerShell" => "PowerShell",
            "OneDriveTrigger" => "OneDrive File Monitor",
            "Condition" => "Condition",
            "Cache" => "Cache",
            "StorageTable" => "Table",
            "StorageClient" => "Client",
            "Loop" => "Loop",
            "VectorDb" => "Vector Store",
            "VectorClient" => "Vector Client",
            "Scheduler" => "Scheduler",
            "Aggregator" => "Aggregator",
            "S2GStorage" => "My Storage",
            "FileDownload" => "File Download",
            "ExcelToJson" => "Excel to JSON",
            "ConnectorToken" => "Connector Token",
            "Orchestrator" => "Orchestrator",
            "Remote" => "Remote Machine",
            "RemoteCommand" => "Remote Command",
            "PdfOcr" => "PDF OCR (Mistral)",
            "Knowledge" => "Knowledge Base",
            _ => "New Node"
        };
    }

    /// <summary>
    /// Gets the default JSON configuration for a node type.
    /// For custom nodes, returns configuration from definition or empty object.
    /// </summary>
    public static string GetDefaultConfiguration(string nodeType)
    {
        // Custom nodes get their default config from the definition
        if (nodeType.StartsWith("Custom_"))
            return "{}";
            
        return nodeType switch
        {
            "HttpListener" => "{\"Method\":\"GET\",\"Path\":\"/api\",\"Port\":8080,\"DefaultResponse\":\"OK\",\"ContentType\":\"text/plain\",\"DefaultStatusCode\":200}",
            "HttpResponse" => "{\"StatusCode\":200,\"Body\":\"Success\",\"ContentType\":\"text/plain\"}",
            "HttpRequest" => "{\"Method\":\"GET\",\"Url\":\"https://api.example.com\"}",
            "SqlServer" => "{\"ConnectionString\":\"\",\"Query\":\"SELECT * FROM Table\"}",
            "Postgresql" => "{\"ConnectionString\":\"\",\"Query\":\"SELECT * FROM Table\"}",
            "MongoDB" => "{\"ConnectionString\":\"\",\"Database\":\"\",\"Collection\":\"\",\"Operation\":\"find\"}",
            "AzureStorage" => "{\"Operation\":\"TableRead\",\"ConnectionString\":\"\",\"TableName\":\"\",\"PartitionKey\":\"\",\"RowKey\":\"\",\"Filter\":\"\",\"MaxResults\":100}",
            "AzureBlob" => "{\"Operation\":\"Read\",\"ConnectionString\":\"\",\"ContainerName\":\"\",\"BlobPath\":\"\",\"Content\":\"\",\"MaxFileSizeBytes\":20971520,\"CreateContainerIfNotExists\":true}",
            "AzureQueueSend" => "{\"ConnectionString\":\"\",\"QueueName\":\"\",\"Message\":\"\",\"TimeToLiveSeconds\":null}",
            "AzureQueueMonitor" => "{\"ConnectionString\":\"\",\"QueueName\":\"\",\"PollIntervalSeconds\":30,\"BatchSize\":1,\"VisibilityTimeoutSeconds\":30}",
            "OpenAI" => "{\"Model\":\"gpt-4o\",\"Temperature\":0.7,\"Prompt\":\"\"}",
            "DeepSeek" => "{\"Model\":\"deepseek-chat\",\"Temperature\":0.7,\"Prompt\":\"\"}",
            "DeepSeekAgent" => "{\"Model\":\"deepseek-chat\",\"MaxToolCalls\":10,\"TimeoutSeconds\":300}",
            "CopilotAgent" => "{\"Model\":\"gpt-4o\",\"MaxToolCalls\":10,\"TimeoutSeconds\":300}",
            "OpenClaw" => "{\"TimeoutSeconds\":300}",
            "Anthropic" => "{\"Model\":\"claude-sonnet-4-20250514\",\"MaxTokens\":1024,\"Prompt\":\"\"}",
            "Gemini" => "{\"Model\":\"gemini-2.0-flash\",\"MaxTokens\":8192,\"Temperature\":1.0,\"Prompt\":\"\"}",
            "Mistral" => "{\"Model\":\"open-mistral-nemo\",\"MaxTokens\":1024,\"Temperature\":0.7,\"Prompt\":\"\"}",
            "Groq" => "{\"Model\":\"llama-3.3-70b-versatile\",\"MaxTokens\":1024,\"Temperature\":0.7,\"Prompt\":\"\"}",
            "LocalLlm" => "{\"BaseUrl\":\"\",\"Model\":\"\",\"MaxTokens\":2048,\"Temperature\":0.7,\"EnableThinking\":false,\"Prompt\":\"\"}",
            "LocalLlmAgent" => "{\"BaseUrl\":\"\",\"Model\":\"\",\"MaxToolCalls\":10,\"TimeoutSeconds\":300,\"EnableThinking\":false}",
            "Queue" => "{\"QueueName\":\"default\",\"MaxSize\":0,\"ExpirationMinutes\":0,\"DelayMs\":0}",
            "PowerShell" => "{\"Script\":\"Write-Output 'Hello'\",\"TimeoutSeconds\":300}",
            "OneDriveTrigger" => "{\"PollIntervalSeconds\":60}",
            "Condition" => "{\"LeftValue\":\"\",\"Operator\":\"==\",\"RightValue\":\"\"}",
            "Cache" => "{\"Operation\":\"Get\",\"PropertyName\":\"\",\"Value\":\"\",\"EnableExpiration\":false,\"ExpirationMinutes\":60}",
            "StorageTable" => "{\"Columns\":[],\"EnableRetention\":false,\"RetentionDays\":30}",
            "StorageClient" => "{\"Operation\":\"Query\",\"MaxResults\":100}",
            "Loop" => "{\"InputArray\":\"\",\"BatchSize\":1,\"DelayBetweenBatches\":0}",
            "VectorDb" => "{}",
            "VectorClient" => "{\"Operation\":\"Search\",\"Limit\":3}",
            "Scheduler" => "{\"ScheduleType\":\"Interval\",\"IntervalValue\":1,\"IntervalUnit\":\"Minutes\",\"TimeOfDay\":\"09:00\",\"Timezone\":\"UTC\"}",
            "Aggregator" => "{\"InputItem\":\"\",\"ThresholdCount\":\"10\",\"SchemaJson\":\"\",\"KeepInvalidItems\":false}",
            "S2GStorage" => "{\"Operation\":\"List\",\"FilePath\":\"\",\"FolderPath\":\"\",\"Content\":\"\",\"MaxFileSizeBytes\":20971520}",
            "FileDownload" => "{\"Url\":\"\",\"FileName\":\"\",\"TimeoutSeconds\":60,\"Headers\":[]}",
            "ExcelToJson" => "{\"ContentBase64\":\"\",\"SheetName\":\"\",\"HeaderDetectionRows\":10,\"TypeInferenceRows\":4,\"IncludeEmptyRows\":false}",
            "ConnectorToken" => "{\"ConnectionId\":\"\"}",
            "Remote" => "{\"ClientId\":\"\",\"Command\":\"\",\"TimeoutSeconds\":60,\"ExpirationMinutes\":5}",
            "RemoteCommand" => "{\"Command\":\"\",\"TimeoutSeconds\":60,\"TargetConnectionTags\":[]}",
            "PdfOcr" => "{\"DocumentSource\":\"\",\"InputType\":\"Url\",\"ExtractTables\":true,\"IncludeImages\":false,\"TimeoutSeconds\":300}",
            "Knowledge" => "{\"Operation\":\"Search\",\"Query\":\"\",\"MaxResults\":20,\"EntityType\":\"\",\"EntityId\":\"\",\"Title\":\"\",\"Content\":\"\",\"Tags\":\"\",\"SourceId\":\"\",\"TargetId\":\"\",\"RelationType\":\"related_to\",\"Direction\":\"both\",\"Depth\":2,\"MaxNodes\":200,\"Bidirectional\":false}",
            _ => "{}"
        };
    }

    /// <summary>
    /// Gets the list of output parameters for a node type (for placeholder generation).
    /// For custom nodes, returns empty list - actual params should be fetched from the definition.
    /// </summary>
    public static List<string> GetOutputParametersForType(string nodeType)
    {
        // Custom nodes get their output parameters from the definition
        if (nodeType.StartsWith("Custom_"))
            return new();
            
        return nodeType switch
        {
            "HttpListener" => new() { "Body", "Method", "Path", "RequestId", "QueryParamsJson", "HeadersJson", "(queryParamName)" },
            "HttpRequest" => new() { "StatusCode", "Body", "IsSuccess", "RequestId" },
            "HttpResponse" => new(),
            "SqlServer" => new() { "Rows", "RowsJson", "RowsXml", "RowsHtml", "FirstRow", "FirstRowJson", "Count", "Columns", "RowsAffected" },
            "Postgresql" => new() { "Result", "RowsAffected" },
            "MongoDB" => new() { "Result" },
            "AzureBlob" => new() { "Content", "BlobPath", "BlobSize", "ContentType", "BytesWritten", "Deleted", "BlobCount", "Success" },
            "AzureQueueSend" => new() { "MessageId", "InsertionTime", "ExpirationTime", "Success" },
            "AzureQueueMonitor" => new() { "MessageId", "MessageText", "InsertionTime", "ExpirationTime", "DequeueCount", "IsJson", "(jsonProperty)" },
            "AzureStorage" => new() { "Result", "ResultXml", "RowCount", "Success" },
            "OpenAI" => new() { "AIResponse", "ModelUsed", "Embedding", "EmbeddingJson" },
            "DeepSeek" => new() { "AIResponse", "ModelUsed" },
            "DeepSeekAgent" => new() { "AIResponse", "ModelUsed", "ToolCallsUsed", "ToolResults", "TotalCost" },
            "CopilotAgent" => new() { "AIResponse", "ModelUsed", "ToolCallsUsed", "ToolResults", "PremiumRequestsUsed" },
            "OpenClaw" => new() { "AIResponse", "ModelUsed", "ToolCallsUsed", "ToolResults", "TotalCost", "TriggerUrl" },
            "Anthropic" => new() { "AIResponse", "ModelUsed", "StopReason", "ToolCalls", "ConversationHistory" },
            "Gemini" => new() { "AIResponse", "ModelUsed", "FinishReason" },
            "Mistral" => new() { "AIResponse", "ModelUsed", "FinishReason", "ConversationHistory" },
            "Groq" => new() { "AIResponse", "ModelUsed", "FinishReason", "ConversationHistory" },
            "LocalLlm" => new() { "Response", "ThinkingContent", "FullMessage", "Model", "PromptTokens", "CompletionTokens", "FinishReason" },
            "LocalLlmAgent" => new() { "AIResponse", "ThinkingContent", "ModelUsed", "ToolCallsUsed", "ToolResults" },
            "Queue" => new() { "QueueOutput", "QueueSize", "TotalEnqueued", "TotalProcessed", "Data" },
            "OneDriveTrigger" => new() { "FileName", "FilePath", "FileId", "DownloadUrl", "FileSize", "CreatedDateTime", "LastModifiedDateTime", "MimeType", "TriggerType" },
            "Condition" => new() { "ConditionResult", "LeftValue", "RightValue" },
            "Cache" => new() { "CacheValue", "CacheKeys", "CacheData", "OperationResult" },
            "StorageTable" => new() { "ColumnsJson", "RecordCount", "TableNodeId" },
            "StorageClient" => new() { "Records", "RecordsJson", "FirstRecord", "Count", "AffectedCount", "InsertedId" },
            "Loop" => new() { "CurrentItem", "CurrentIndex", "TotalCount", "IsFirstItem", "IsLastItem", "BatchNumber", "ProcessedCount", "(itemProperty)" },
            "VectorDb" => new() { "DocumentCount", "StoreNodeId" },
            "VectorClient" => new() { "Results", "ResultsJson", "FirstResult", "TopSimilarity", "Count", "InsertedId", "OperationResult" },
            "Scheduler" => new() { "SchedulerTriggeredAt", "SchedulerLocalTime", "SchedulerNextRun", "SchedulerType", "SchedulerTimezone", "SchedulerExpired" },
            "Aggregator" => new() { "AggregatedItems", "AggregatedItemsJson", "ItemCount", "BufferSize", "InvalidItem", "InvalidReason", "IsThresholdReached" },
            "S2GStorage" => new() { "Content", "FilePath", "FileSize", "ContentType", "BytesWritten", "Deleted", "FileCount", "Success", "FolderName", "Files", "Folders" },
            "FileDownload" => new() { "Success", "FileName", "ContentType", "FileSize", "ContentBase64", "ErrorMessage" },
            "ExcelToJson" => new() { "Json", "Schema", "SheetNames", "RowCount", "Success", "ErrorMessage" },
            "ConnectorToken" => new() { "AccessToken", "Provider", "Email", "Scopes", "TokenExpiry", "Success", "ErrorMessage" },
            "Orchestrator" => new() { "FinalResult", "IterationCount", "IsSuccess", "AgentResponses", "LastEvaluation", "ExecutionLog", "TotalToolCalls" },
            "Remote" => new() { "CommandOutput", "ExitCode", "ExecutionId", "ClientId", "Hostname", "OS", "CpuUsage", "MemoryUsage", "DiskUsage", "LastSeen", "IsOnline", "QueuedCommands", "Response" },
            "RemoteCommand" => new() { "Results", "ResultsJson", "SuccessCount", "TimeoutCount", "TotalCount" },
            "PdfOcr" => new() { "Text", "Markdown", "Tables", "PageCount", "Cost" },
            "Knowledge" => new() { "Result", "ResultJson", "EntityId", "RelationsJson", "GraphJson", "Success" },
            _ => new()
        };
    }

    /// <summary>
    /// Snaps a coordinate value to the grid.
    /// </summary>
    public static double SnapToGrid(double value)
    {
        return Math.Round(value / DesignerConstants.GridSize) * DesignerConstants.GridSize;
    }
}
