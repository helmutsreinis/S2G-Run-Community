using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class GroqNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public GroqNode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "Groq";

    public override List<string> GetOutputParameters() => 
        new() { "AIResponse", "ModelUsed", "FinishReason", "ConversationHistory" };

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<GroqConfig>(node.Configuration ?? "{}") ?? new();

        // Get API key - node override or from settings
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "Groq_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            Log(node, NodeLogLevel.Error, "No Groq API key configured");
            return new NodeExecutionResult { Success = false };
        }

        var model = config.Model ?? "llama-3.3-70b-versatile";

        try
        {
            // Build messages array
            var messages = new List<object>();

            // Add system prompt if provided
            if (!string.IsNullOrEmpty(config.SystemPrompt))
            {
                messages.Add(new { role = "system", content = config.SystemPrompt });
            }

            // Add conversation history if in conversation mode
            if (config.ConversationMode && config.ConversationHistory?.Count > 0)
            {
                foreach (var historyMsg in config.ConversationHistory)
                {
                    messages.Add(historyMsg);
                }
            }

            // Add current user message
            messages.Add(new { role = "user", content = config.Prompt });

            var requestBody = new
            {
                model = model,
                messages = messages,
                max_tokens = config.MaxTokens,
                temperature = config.Temperature
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log(node, NodeLogLevel.Error, $"Groq API error: {response.StatusCode}", responseBody);
                return new NodeExecutionResult { Success = false };
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Extract response content
            var aiResponse = "";
            var finishReason = "";
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    aiResponse = content.GetString() ?? "";
                }
                if (firstChoice.TryGetProperty("finish_reason", out var fr))
                {
                    finishReason = fr.GetString() ?? "";
                }
            }

            // Extract token usage - Groq uses prompt_tokens and completion_tokens
            long promptTokens = 0, completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt))
                    promptTokens = pt.GetInt64();
                if (usage.TryGetProperty("completion_tokens", out var ct))
                    completionTokens = ct.GetInt64();
            }

            // Calculate cost
            var inputRate = GetInputRate(model);
            var outputRate = GetOutputRate(model);
            var runCost = (promptTokens * inputRate / 1_000_000) + (completionTokens * outputRate / 1_000_000);

            // Accumulate costs
            config.Cost += runCost;
            config.InputTokens += promptTokens;
            config.OutputTokens += completionTokens;

            // Update conversation history if in conversation mode
            if (config.ConversationMode)
            {
                config.ConversationHistory ??= new List<Dictionary<string, string>>();
                config.ConversationHistory.Add(new Dictionary<string, string> { { "role", "user" }, { "content", config.Prompt } });
                config.ConversationHistory.Add(new Dictionary<string, string> { { "role", "assistant" }, { "content", aiResponse } });
            }

            // Update node configuration
            node.Configuration = JsonSerializer.Serialize(config);
            _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);

            Log(node, NodeLogLevel.Info, $"Received response from Groq ({aiResponse.Length} chars) (Cost: ${runCost:F5})",
                JsonSerializer.Serialize(new
                {
                    Model = model,
                    ResponseLength = aiResponse.Length,
                    FinishReason = finishReason,
                    Usage = new { PromptTokens = promptTokens, CompletionTokens = completionTokens },
                    RunCost = runCost,
                    TotalCost = config.Cost
                }));

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    ["AIResponse"] = aiResponse,
                    ["ModelUsed"] = model,
                    ["FinishReason"] = finishReason,
                    ["ConversationHistory"] = config.ConversationHistory != null 
                        ? JsonSerializer.Serialize(config.ConversationHistory) 
                        : null
                }
            };
        }
        catch (TaskCanceledException)
        {
            Log(node, NodeLogLevel.Error, $"Request timed out after {config.TimeoutSeconds} seconds");
            return new NodeExecutionResult { Success = false };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Groq error: {ex.Message}");
            return new NodeExecutionResult { Success = false };
        }
    }

    /// <summary>
    /// Get input token rate per million tokens for the specified model
    /// </summary>
    private static double GetInputRate(string model) => model switch
    {
        // GPT OSS Models
        "openai/gpt-oss-20b" => 0.075,
        "openai/gpt-oss-safeguard-20b" => 0.075,
        "openai/gpt-oss-120b" => 0.15,
        
        // Partner Models
        "moonshotai/kimi-k2-instruct-0905" => 1.00,
        
        // Llama 4 Models
        "llama-4-scout-17b-16e" => 0.11,
        "llama-4-maverick-17b-128e" => 0.20,
        "llama-guard-4-12b" => 0.20,
        
        // Other Models
        "qwen-3-32b" => 0.29,
        "llama-3.3-70b-versatile" => 0.59,
        "llama-3.1-8b-instant" => 0.05,
        
        _ => 0.20 // Default fallback
    };

    /// <summary>
    /// Get output token rate per million tokens for the specified model
    /// </summary>
    private static double GetOutputRate(string model) => model switch
    {
        // GPT OSS Models
        "openai/gpt-oss-20b" => 0.30,
        "openai/gpt-oss-safeguard-20b" => 0.30,
        "openai/gpt-oss-120b" => 0.60,
        
        // Partner Models
        "moonshotai/kimi-k2-instruct-0905" => 3.00,
        
        // Llama 4 Models
        "llama-4-scout-17b-16e" => 0.34,
        "llama-4-maverick-17b-128e" => 0.60,
        "llama-guard-4-12b" => 0.20,
        
        // Other Models
        "qwen-3-32b" => 0.59,
        "llama-3.3-70b-versatile" => 0.79,
        "llama-3.1-8b-instant" => 0.08,
        
        _ => 0.40 // Default fallback
    };
}

public class GroqConfig
{
    public string? ApiKey { get; set; }
    public string? Model { get; set; } = "llama-3.3-70b-versatile";
    public string? SystemPrompt { get; set; }
    public string Prompt { get; set; } = "";
    public int MaxTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.7;
    public int TimeoutSeconds { get; set; } = 300;
    public bool ConversationMode { get; set; } = false;
    public List<Dictionary<string, string>>? ConversationHistory { get; set; }
    
    // Cost tracking
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;
}
