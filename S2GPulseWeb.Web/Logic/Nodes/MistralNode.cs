using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class MistralNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public MistralNode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "Mistral";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<MistralConfig>(node.Configuration ?? "{}") ?? new MistralConfig();
        double? lastRunCost = null;

        // Preserve original prompt values (with placeholders) for later restoration
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        // Fetch API Key from config or UserSecretService
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "Mistral_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            Log(node, NodeLogLevel.Error, "Mistral API Key is missing. Please configure it in Settings.");
            return new NodeExecutionResult { Success = false, ErrorMessage = "Mistral API Key is missing for this user. Please configure it in Settings." };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);
        var model = config.Model ?? "open-mistral-nemo";
        var maxTokens = config.MaxTokens > 0 ? config.MaxTokens : 1024;
        var temperature = config.Temperature;

        // Build messages array
        var messages = new List<object>();
        
        // Add system message if provided
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        // Handle conversational mode - use cached history from config
        if (config.ConversationMode && config.ConversationHistory != null && config.ConversationHistory.Count > 0)
        {
            foreach (var msg in config.ConversationHistory)
            {
                messages.Add(new { role = msg.Role, content = msg.Content });
            }
        }
        // Legacy: also support explicit Messages property for backwards compatibility
        else if (config.Messages != null && config.Messages.Count > 0)
        {
            foreach (var msg in config.Messages)
            {
                messages.Add(new { role = msg.Role, content = ReplacePlaceholders(msg.Content, inputData) });
            }
        }
        
        // Add current user message
        messages.Add(new { role = "user", content = prompt });

        // Build request body - Mistral uses OpenAI-compatible format
        var requestBody = new
        {
            model = model,
            messages = messages.ToArray(),
            max_tokens = maxTokens,
            temperature = temperature
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/chat/completions")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        // Log request details
        var requestJson = JsonSerializer.Serialize(new
        {
            Model = model,
            MaxTokens = maxTokens,
            Temperature = temperature,
            PromptLength = prompt.Length,
            PromptPreview = prompt.Length > 500 ? prompt.Substring(0, 500) + "..." : prompt
        }, new JsonSerializerOptions { WriteIndented = true });
        Log(node, NodeLogLevel.Info, $"Sending request to Mistral ({model})", requestJson);

        try
        {
            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log(node, NodeLogLevel.Error, $"Mistral API returned {(int)response.StatusCode}: {response.ReasonPhrase}", responseContent);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"Mistral API error ({(int)response.StatusCode}): {responseContent}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            
            // Extract response content - Mistral uses OpenAI-compatible format
            var choices = jsonResponse.GetProperty("choices");
            var aiResponse = "";
            var finishReason = "";
            
            if (choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                finishReason = firstChoice.GetProperty("finish_reason").GetString() ?? "";
                var message = firstChoice.GetProperty("message");
                aiResponse = message.GetProperty("content").GetString() ?? "";
            }

            var modelUsed = jsonResponse.TryGetProperty("model", out var modelProp) 
                ? modelProp.GetString() ?? model 
                : model;

            // Calculate cost
            string? usageInfo = null;
            if (jsonResponse.TryGetProperty("usage", out var usage))
            {
                usageInfo = JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true });

                if (usage.TryGetProperty("prompt_tokens", out var inputTokensElement) &&
                    usage.TryGetProperty("completion_tokens", out var outputTokensElement))
                {
                    long inputTokens = inputTokensElement.GetInt64();
                    long outputTokens = outputTokensElement.GetInt64();

                    // Pricing per million tokens based on model
                    double inputRate = GetInputRate(model);
                    double outputRate = GetOutputRate(model);

                    double cost = (inputTokens / 1000000.0) * inputRate + (outputTokens / 1000000.0) * outputRate;

                    // Update config with accumulated cost
                    config.Cost += cost;
                    config.InputTokens += inputTokens;
                    config.OutputTokens += outputTokens;

                    // Restore original prompts with placeholders before saving
                    config.Prompt = originalPrompt;
                    config.SystemPrompt = originalSystemPrompt;

                    node.Configuration = JsonSerializer.Serialize(config);

                    // Notify UI about configuration change for live cost updates
                    _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);

                    lastRunCost = cost;
                }
            }

            // If conversation mode is enabled, cache the conversation history
            string? conversationHistoryJson = null;
            if (config.ConversationMode)
            {
                // Initialize history if null
                config.ConversationHistory ??= new List<MistralMessage>();
                
                // Append current user message
                config.ConversationHistory.Add(new MistralMessage { Role = "user", Content = prompt });
                
                // Append assistant response
                config.ConversationHistory.Add(new MistralMessage { Role = "assistant", Content = aiResponse });
                
                // Serialize for output
                conversationHistoryJson = JsonSerializer.Serialize(config.ConversationHistory);
                
                // Save updated config with history
                config.Prompt = originalPrompt;
                config.SystemPrompt = originalSystemPrompt;
                node.Configuration = JsonSerializer.Serialize(config);
                _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);
            }

            // Log success with cost
            var detail = JsonSerializer.Serialize(new
            {
                Model = modelUsed,
                ResponseLength = aiResponse.Length,
                FinishReason = finishReason,
                Usage = usageInfo,
                RunCost = lastRunCost,
                TotalCost = config.Cost
            }, new JsonSerializerOptions { WriteIndented = true });

            var costString = lastRunCost.HasValue ? $" (Cost: ${lastRunCost.Value:F5})" : "";
            Log(node, NodeLogLevel.Info, $"Received response from Mistral ({aiResponse.Length} chars){costString}", detail);

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AIResponse", aiResponse },
                    { "ModelUsed", modelUsed },
                    { "FinishReason", finishReason },
                    { "ConversationHistory", conversationHistoryJson }
                }
            };
        }
        catch (TaskCanceledException ex)
        {
            Log(node, NodeLogLevel.Error, $"Request timeout: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Request timeout: {ex.Message}" };
        }
        catch (HttpRequestException ex)
        {
            Log(node, NodeLogLevel.Error, $"Network error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Network error: {ex.Message}" };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Mistral API error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Mistral API error: {ex.Message}" };
        }
    }

    public override List<string> GetOutputParameters()
    {
        return new List<string> { "AIResponse", "ModelUsed", "FinishReason", "ConversationHistory" };
    }

    private static double GetInputRate(string model)
    {
        // Pricing per million tokens (from Mistral pricing table)
        return model switch
        {
            // Premier Models (State-of-the-Art)
            "mistral-large-latest" => 2.00,
            "pixtral-large-latest" => 2.00,
            "mistral-medium-latest" => 2.70,
            "codestral-latest" => 0.20,
            "codestral-mamba-latest" => 0.25,
            
            // General Purpose (Open Source / Edge)
            "mistral-small-latest" => 0.20,
            "ministral-8b-latest" => 0.10,
            "ministral-3b-latest" => 0.04,
            "open-mistral-nemo" => 0.15,
            "open-mixtral-8x22b" => 2.00,
            "open-mixtral-8x7b" => 0.70,
            "open-mistral-7b" => 0.25,
            
            _ => 0.15 // Default to Nemo pricing
        };
    }

    private static double GetOutputRate(string model)
    {
        return model switch
        {
            // Premier Models (State-of-the-Art)
            "mistral-large-latest" => 6.00,
            "pixtral-large-latest" => 6.00,
            "mistral-medium-latest" => 8.10,
            "codestral-latest" => 0.60,
            "codestral-mamba-latest" => 0.25,
            
            // General Purpose (Open Source / Edge)
            "mistral-small-latest" => 0.60,
            "ministral-8b-latest" => 0.10,
            "ministral-3b-latest" => 0.04,
            "open-mistral-nemo" => 0.15,
            "open-mixtral-8x22b" => 6.00,
            "open-mixtral-8x7b" => 0.70,
            "open-mistral-7b" => 0.25,
            
            _ => 0.15 // Default to Nemo pricing
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
}

public class MistralConfig
{
    public string? Model { get; set; } = "open-mistral-nemo";
    public int MaxTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.7;
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;
    
    // For conversational turns
    public List<MistralMessage>? Messages { get; set; }
    
    // Conversation mode - caches history within node
    public bool ConversationMode { get; set; } = false;
    public List<MistralMessage>? ConversationHistory { get; set; }
}

public class MistralMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}
