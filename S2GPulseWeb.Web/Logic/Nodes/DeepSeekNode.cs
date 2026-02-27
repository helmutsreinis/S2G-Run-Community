using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class DeepSeekNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public DeepSeekNode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "DeepSeek";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<DeepSeekConfig>(node.Configuration ?? "{}") ?? new DeepSeekConfig();
        double? lastRunCost = null;
        
        // Preserve original prompt values (with placeholders) for later restoration
        // This prevents overwriting {{placeholders}} with resolved values when saving costs
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "DeepSeek_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            return new NodeExecutionResult { Success = false, ErrorMessage = "DeepSeek API Key is missing for this user. Please configure it in Settings." };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);
        var model = config.Model ?? "deepseek-chat";
        
        // ═══════════════════════════════════════════════════════════════════════════════
        // ORCHESTRATOR CONTEXT INJECTION
        // When called by Orchestrator as Steering AI, use injected prompts
        // ═══════════════════════════════════════════════════════════════════════════════
        if (inputData.TryGetValue("_OrchestratorSystemPromptOverride", out var sysOverride) && sysOverride != null)
        {
            systemPrompt = sysOverride.ToString() ?? systemPrompt;
            Log(node, NodeLogLevel.Info, "Using Orchestrator-injected system prompt", 
                Truncate(systemPrompt, 200));
        }
        
        if (inputData.TryGetValue("_OrchestratorPromptAppend", out var promptAppend) && promptAppend != null)
        {
            // When orchestrator injects prompt, use it as the user message
            prompt = (string.IsNullOrEmpty(prompt) ? "" : prompt + "\n\n") + promptAppend.ToString();
            Log(node, NodeLogLevel.Info, "Using Orchestrator-injected user prompt", 
                Truncate(prompt, 200));
        }

        // JSON mode requires prompts to contain "JSON" reference
        if (config.JsonMode)
        {
            var combinedPrompts = (systemPrompt + " " + prompt).ToUpperInvariant();
            if (!combinedPrompts.Contains("JSON"))
            {
                Log(node, NodeLogLevel.Error, "JSON mode requires prompts to mention 'JSON'. DeepSeek API will reject requests without this.");
                return new NodeExecutionResult 
                { 
                    Success = false, 
                    ErrorMessage = "JSON mode is enabled but prompts don't contain 'JSON'. Add 'JSON' to your system prompt or user prompt (e.g., 'Reply in JSON format')." 
                };
            }
        }

        // Build messages array
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }
        messages.Add(new { role = "user", content = prompt });

        // Build request body
        object requestBody;
        if (config.JsonMode)
        {
            requestBody = new
            {
                model = model,
                messages = messages.ToArray(),
                stream = false,
                response_format = new { type = "json_object" }
            };
        }
        else
        {
            requestBody = new
            {
                model = model,
                messages = messages.ToArray(),
                stream = false
            };
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        // Log request details
        var requestJson = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
        Log(node, NodeLogLevel.Info, $"Sending request to DeepSeek ({model})", requestJson);

        try
        {
            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                // Log the actual error response from DeepSeek
                Log(node, NodeLogLevel.Error, $"DeepSeek API returned {(int)response.StatusCode}: {response.ReasonPhrase}", responseContent);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"DeepSeek API error ({(int)response.StatusCode}): {responseContent}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            var aiResponse = jsonResponse.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

            // Calculate cost
            string? usageInfo = null;
            if (jsonResponse.TryGetProperty("usage", out var usage))
            {
                usageInfo = JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true });

                if (usage.TryGetProperty("prompt_tokens", out var promptTokensElement) &&
                    usage.TryGetProperty("completion_tokens", out var completionTokensElement))
                {
                    long promptTokens = promptTokensElement.GetInt64();
                    long completionTokens = completionTokensElement.GetInt64();

                    // Pricing for both chat and reasoner (V3)
                    double inputRate = 0.28;  // cache miss price (conservative)
                    double outputRate = 0.42;

                    // If cache hit info is available in future, we could adjust inputRate to 0.028

                    double cost = (promptTokens / 1000000.0) * inputRate + (completionTokens / 1000000.0) * outputRate;

                    // Update config with accumulated cost
                    config.Cost += cost;
                    config.InputTokens += promptTokens;
                    config.OutputTokens += completionTokens;
                    
                    // Restore original prompts with placeholders before saving
                    // This ensures {{placeholders}} are preserved in the configuration
                    config.Prompt = originalPrompt;
                    config.SystemPrompt = originalSystemPrompt;

                    node.Configuration = JsonSerializer.Serialize(config);
                    
                    // Notify UI about configuration change for live cost updates
                    _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);

                    // Store last run cost for logging
                    lastRunCost = cost;
                }
            }

            // Log success with cost
            var detail = JsonSerializer.Serialize(new
            {
                Model = model,
                ResponseLength = aiResponse.Length,
                Usage = usageInfo,
                RunCost = lastRunCost,
                TotalCost = config.Cost
            }, new JsonSerializerOptions { WriteIndented = true });

            var costString = lastRunCost.HasValue ? $" (Cost: ${lastRunCost.Value:F5})" : "";
            Log(node, NodeLogLevel.Info, $"Received response from DeepSeek ({aiResponse.Length} chars){costString}", detail);
            
            // Log actual AI response content for debugging
            var truncatedResponse = aiResponse.Length > 500 ? aiResponse[..500] + "..." : aiResponse;
            Log(node, NodeLogLevel.Info, "AIResponse content", truncatedResponse);

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AIResponse", aiResponse },
                    { "ModelUsed", model }
                }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"DeepSeek API error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"DeepSeek API error: {ex.Message}" };
        }
    }

    public override List<string> GetOutputParameters()
    {
        return new List<string> { "AIResponse", "ModelUsed" };
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
    
    private string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}

public class DeepSeekConfig
{
    public string? Model { get; set; } = "deepseek-chat";
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public bool JsonMode { get; set; } = false;
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300; // 5 minute default for long AI requests
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;
}
