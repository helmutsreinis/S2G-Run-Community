using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class GeminiNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public GeminiNode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "Gemini";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<GeminiConfig>(node.Configuration ?? "{}") ?? new GeminiConfig();
        double? lastRunCost = null;

        // Preserve original prompt values (with placeholders) for later restoration
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        // Fetch API Key from config or UserSecretService
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "Gemini_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            Log(node, NodeLogLevel.Error, "Gemini API Key is missing. Please configure it in Settings.");
            return new NodeExecutionResult { Success = false, ErrorMessage = "Gemini API Key is missing. Please configure it in Settings." };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);
        var model = config.Model ?? "gemini-2.0-flash";

        // Build contents array for Gemini API
        var contents = new List<object>();
        
        // Handle conversational history if provided
        if (config.Messages != null && config.Messages.Count > 0)
        {
            foreach (var msg in config.Messages)
            {
                contents.Add(new 
                { 
                    role = msg.Role == "assistant" ? "model" : msg.Role, 
                    parts = new[] { new { text = ReplacePlaceholders(msg.Content, inputData) } } 
                });
            }
        }
        
        // Add current user message
        contents.Add(new { role = "user", parts = new[] { new { text = prompt } } });

        // Build request body
        object requestBody;
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            requestBody = new
            {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = contents.ToArray(),
                generationConfig = new
                {
                    maxOutputTokens = config.MaxTokens > 0 ? config.MaxTokens : 8192,
                    temperature = config.Temperature
                }
            };
        }
        else
        {
            requestBody = new
            {
                contents = contents.ToArray(),
                generationConfig = new
                {
                    maxOutputTokens = config.MaxTokens > 0 ? config.MaxTokens : 8192,
                    temperature = config.Temperature
                }
            };
        }

        // Gemini API endpoint with API key as query parameter
        var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("Accept", "application/json");

        // Log request details
        var requestJson = JsonSerializer.Serialize(new
        {
            Model = model,
            MaxTokens = config.MaxTokens,
            PromptLength = prompt.Length,
            PromptPreview = prompt.Length > 500 ? prompt.Substring(0, 500) + "..." : prompt
        }, new JsonSerializerOptions { WriteIndented = true });
        Log(node, NodeLogLevel.Info, $"Sending request to Gemini ({model})", requestJson);

        try
        {
            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log(node, NodeLogLevel.Error, $"Gemini API returned {(int)response.StatusCode}: {response.ReasonPhrase}", responseContent);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"Gemini API error ({(int)response.StatusCode}): {responseContent}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            
            // Extract response content from Gemini format
            var aiResponse = "";
            var finishReason = "";
            
            if (jsonResponse.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var firstCandidate = candidates[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                        {
                            aiResponse += textProp.GetString() ?? "";
                        }
                    }
                }
                
                if (firstCandidate.TryGetProperty("finishReason", out var finishReasonProp))
                {
                    finishReason = finishReasonProp.GetString() ?? "";
                }
            }

            // Calculate cost from usage metadata
            string? usageInfo = null;
            if (jsonResponse.TryGetProperty("usageMetadata", out var usage))
            {
                usageInfo = JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true });

                long inputTokens = 0;
                long outputTokens = 0;
                
                if (usage.TryGetProperty("promptTokenCount", out var promptTokens))
                {
                    inputTokens = promptTokens.GetInt64();
                }
                if (usage.TryGetProperty("candidatesTokenCount", out var candidateTokens))
                {
                    outputTokens = candidateTokens.GetInt64();
                }

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

            // Log success with cost
            var detail = JsonSerializer.Serialize(new
            {
                Model = model,
                ResponseLength = aiResponse.Length,
                FinishReason = finishReason,
                Usage = usageInfo,
                RunCost = lastRunCost,
                TotalCost = config.Cost
            }, new JsonSerializerOptions { WriteIndented = true });

            var costString = lastRunCost.HasValue ? $" (Cost: ${lastRunCost.Value:F5})" : "";
            Log(node, NodeLogLevel.Info, $"Received response from Gemini ({aiResponse.Length} chars){costString}", detail);

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AIResponse", aiResponse },
                    { "ModelUsed", model },
                    { "FinishReason", finishReason }
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
            Log(node, NodeLogLevel.Error, $"Gemini API error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Gemini API error: {ex.Message}" };
        }
    }

    public override List<string> GetOutputParameters()
    {
        return new List<string> { "AIResponse", "ModelUsed", "FinishReason" };
    }

    private static double GetInputRate(string model)
    {
        // Pricing per million tokens (from Google Gemini pricing)
        return model switch
        {
            // Gemini 2.5 Pro
            "gemini-2.5-pro" or "gemini-2.5-pro-preview-05-06" => 1.25,
            // Gemini 2.5 Flash
            "gemini-2.5-flash" or "gemini-2.5-flash-preview-05-20" => 0.15,
            // Gemini 2.5 Flash-Lite
            "gemini-2.5-flash-lite" or "gemini-2.5-flash-lite-preview-06-17" => 0.10,
            // Gemini 2.0 Flash
            "gemini-2.0-flash" or "gemini-2.0-flash-001" => 0.10,
            // Gemini 2.0 Flash-Lite
            "gemini-2.0-flash-lite" or "gemini-2.0-flash-lite-001" => 0.075,
            // Gemini 1.5 Pro
            "gemini-1.5-pro" or "gemini-1.5-pro-latest" => 1.25,
            // Gemini 1.5 Flash
            "gemini-1.5-flash" or "gemini-1.5-flash-latest" => 0.075,
            _ => 0.10 // Default to Flash pricing
        };
    }

    private static double GetOutputRate(string model)
    {
        return model switch
        {
            // Gemini 2.5 Pro
            "gemini-2.5-pro" or "gemini-2.5-pro-preview-05-06" => 10.00,
            // Gemini 2.5 Flash
            "gemini-2.5-flash" or "gemini-2.5-flash-preview-05-20" => 0.60,
            // Gemini 2.5 Flash-Lite
            "gemini-2.5-flash-lite" or "gemini-2.5-flash-lite-preview-06-17" => 0.40,
            // Gemini 2.0 Flash
            "gemini-2.0-flash" or "gemini-2.0-flash-001" => 0.40,
            // Gemini 2.0 Flash-Lite
            "gemini-2.0-flash-lite" or "gemini-2.0-flash-lite-001" => 0.30,
            // Gemini 1.5 Pro
            "gemini-1.5-pro" or "gemini-1.5-pro-latest" => 5.00,
            // Gemini 1.5 Flash
            "gemini-1.5-flash" or "gemini-1.5-flash-latest" => 0.30,
            _ => 0.40 // Default to Flash pricing
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

public class GeminiConfig
{
    public string? Model { get; set; } = "gemini-2.0-flash";
    public int MaxTokens { get; set; } = 8192;
    public double Temperature { get; set; } = 1.0;
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;
    
    // For conversational turns
    public List<GeminiMessage>? Messages { get; set; }
}

public class GeminiMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}
