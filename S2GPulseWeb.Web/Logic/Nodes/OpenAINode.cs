using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class OpenAINode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public OpenAINode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "OpenAI";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<OpenAIConfig>(node.Configuration ?? "{}") ?? new OpenAIConfig();
        
        // Route to embedding or chat based on operation
        if (config.Operation == "Embedding")
        {
            return await ExecuteEmbeddingAsync(node, inputData, userId, config);
        }
        
        return await ExecuteChatAsync(node, inputData, userId, config);
    }

    private async Task<NodeExecutionResult> ExecuteChatAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId, OpenAIConfig config)
    {
        double? lastRunCost = null;
        
        // Preserve original prompt values (with placeholders) for later restoration
        // This prevents overwriting {{placeholders}} with resolved values when saving costs
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;
        
        // Fetch API Key from config or UserSecretService
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "OpenAI_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            Log(node, NodeLogLevel.Error, "OpenAI API Key is missing. Please configure it in Settings.");
            return new NodeExecutionResult { Success = false, ErrorMessage = "OpenAI API Key is missing for this user. Please configure it in Settings." };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);
        var model = config.Model ?? "gpt-4o";

        // Build messages array
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }
        messages.Add(new { role = "user", content = prompt });

        var requestBody = new
        {
            model = model,
            messages = messages.ToArray(),
            temperature = config.Temperature
        };

        // Log request details
        var requestDetail = JsonSerializer.Serialize(new
        {
            Model = model,
            Temperature = config.Temperature,
            PromptLength = prompt.Length,
            PromptPreview = prompt.Length > 500 ? prompt.Substring(0, 500) + "..." : prompt
        }, new JsonSerializerOptions { WriteIndented = true });
        Log(node, NodeLogLevel.Info, $"Sending request to OpenAI ({model})", requestDetail);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var errorDetail = JsonSerializer.Serialize(new
                {
                    StatusCode = (int)response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    ResponseBody = responseContent
                }, new JsonSerializerOptions { WriteIndented = true });
                Log(node, NodeLogLevel.Error, $"OpenAI API Error: {response.StatusCode}", errorDetail);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"OpenAI API Error: {response.StatusCode} - {response.ReasonPhrase}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            var aiResponse = jsonResponse.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            
            // Extract usage info if available
            string? usageInfo = null;
            if (jsonResponse.TryGetProperty("usage", out var usage))
            {
                usageInfo = JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true });

                // Calculate cost
                if (usage.TryGetProperty("prompt_tokens", out var promptTokensElement) &&
                    usage.TryGetProperty("completion_tokens", out var completionTokensElement))
                {
                    long promptTokens = promptTokensElement.GetInt64();
                    long completionTokens = completionTokensElement.GetInt64();

                    double inputRate = 0;
                    double outputRate = 0;

                    switch (model)
                    {
                        case "gpt-4o":
                            inputRate = 2.50;
                            outputRate = 10.00;
                            break;
                        case "gpt-4o-mini":
                            inputRate = 0.15;
                            outputRate = 0.60;
                            break;
                        case "o1":
                            inputRate = 15.00;
                            outputRate = 60.00;
                            break;
                        case "o1-mini":
                            inputRate = 1.10;
                            outputRate = 4.40;
                            break;
                    }

                    double cost = (promptTokens / 1000000.0) * inputRate + (completionTokens / 1000000.0) * outputRate;

                    // Update config with accumulated cost
                    config.Cost += cost;
                    config.InputTokens += promptTokens;
                    config.OutputTokens += completionTokens;
                    
                    // Restore original prompts with placeholders before saving
                    // This ensures {{placeholders}} are preserved in the configuration
                    config.Prompt = originalPrompt;
                    config.SystemPrompt = originalSystemPrompt;

                    // Update the node configuration
                    node.Configuration = JsonSerializer.Serialize(config);
                    
                    // Notify UI about configuration change for live cost updates
                    _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);

                    // Store last run cost for logging
                    lastRunCost = cost;
                }
            }

            // Log response details
            var responseDetail = JsonSerializer.Serialize(new
            {
                StatusCode = (int)response.StatusCode,
                Model = model,
                ResponseLength = aiResponse.Length,
                ResponsePreview = aiResponse.Length > 500 ? aiResponse.Substring(0, 500) + "..." : aiResponse,
                Usage = usageInfo,
                RunCost = lastRunCost,
                TotalCost = config.Cost
            }, new JsonSerializerOptions { WriteIndented = true });

            var costString = lastRunCost.HasValue ? $" (Cost: ${lastRunCost.Value:F5})" : "";
            Log(node, NodeLogLevel.Info, $"Received response from OpenAI ({aiResponse.Length} chars){costString}", responseDetail);

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
            Log(node, NodeLogLevel.Error, $"Unexpected error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Unexpected error: {ex.Message}" };
        }
    }

    public override List<string> GetOutputParameters()
    {
        return new List<string> { "AIResponse", "ModelUsed", "Embedding", "EmbeddingJson" };
    }

    private async Task<NodeExecutionResult> ExecuteEmbeddingAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId, OpenAIConfig config)
    {
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "OpenAI_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            Log(node, NodeLogLevel.Error, "OpenAI API Key is missing.");
            return new NodeExecutionResult { Success = false, ErrorMessage = "OpenAI API Key is missing." };
        }

        var text = ReplacePlaceholders(config.Prompt, inputData);
        var embeddingModel = config.EmbeddingModel ?? "text-embedding-3-small";

        var requestBody = new { model = embeddingModel, input = text };
        
        Log(node, NodeLogLevel.Info, $"Creating embedding with {embeddingModel} ({text.Length} chars)");

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log(node, NodeLogLevel.Error, $"OpenAI Embedding Error: {response.StatusCode}", responseContent);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"OpenAI Embedding Error: {response.StatusCode}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            var embeddingArray = jsonResponse.GetProperty("data")[0].GetProperty("embedding");
            var embeddingJson = embeddingArray.ToString();
            
            // Track usage if available
            if (jsonResponse.TryGetProperty("usage", out var usage))
            {
                var totalTokens = usage.GetProperty("total_tokens").GetInt64();
                config.InputTokens += totalTokens;
                // Embedding pricing: $0.02/1M tokens for small, $0.13/1M for large
                double costPerMillion = embeddingModel.Contains("large") ? 0.13 : 0.02;
                config.Cost += (totalTokens / 1_000_000.0) * costPerMillion;
                
                // Save and notify for live cost updates
                node.Configuration = JsonSerializer.Serialize(config);
                _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);
            }

            Log(node, NodeLogLevel.Info, $"Embedding created successfully ({embeddingArray.GetArrayLength()} dimensions)");

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "Embedding", embeddingJson },
                    { "EmbeddingJson", embeddingJson },
                    { "AIResponse", $"Embedding created: {embeddingArray.GetArrayLength()} dimensions" },
                    { "ModelUsed", embeddingModel }
                }
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Embedding error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = ex.Message };
        }
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

public class OpenAIConfig
{
    public string Operation { get; set; } = "Chat";
    public string? Model { get; set; }
    public string? EmbeddingModel { get; set; } = "text-embedding-3-small";
    public double Temperature { get; set; } = 0.7;
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300; // 5 minute default for long AI requests
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;
}
