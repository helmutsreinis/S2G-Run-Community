using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

public class AnthropicNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;
    private readonly UserSecretService _secretService;

    public AnthropicNode(HttpClient httpClient, UserSecretService secretService, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
        _secretService = secretService;
    }

    public override string NodeType => "Anthropic";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(WorkflowNode node, Dictionary<string, object?> inputData, string userId)
    {
        var config = JsonSerializer.Deserialize<AnthropicConfig>(node.Configuration ?? "{}") ?? new AnthropicConfig();
        double? lastRunCost = null;

        // Preserve original prompt values (with placeholders) for later restoration
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        // Fetch API Key from config or UserSecretService
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : await _secretService.GetSecretAsync(userId, "Anthropic_ApiKey");

        if (string.IsNullOrEmpty(apiKey))
        {
            Log(node, NodeLogLevel.Error, "Anthropic API Key is missing. Please configure it in Settings.");
            return new NodeExecutionResult { Success = false, ErrorMessage = "Anthropic API Key is missing for this user. Please configure it in Settings." };
        }

        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);
        var model = config.Model ?? "claude-sonnet-4-20250514";
        var maxTokens = config.MaxTokens > 0 ? config.MaxTokens : 1024;

        // Build messages array
        var messages = new List<object>();
        
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
        
        // Add current user message - with optional image support
        var imageBase64 = ReplacePlaceholders(config.ImageBase64 ?? "", inputData);
        var mediaType = config.MediaType ?? "image/png";
        
        if (!string.IsNullOrEmpty(imageBase64))
        {
            // Multi-modal message with image and text
            var contentParts = new List<object>
            {
                new 
                { 
                    type = "image", 
                    source = new 
                    { 
                        type = "base64", 
                        media_type = mediaType, 
                        data = imageBase64 
                    } 
                }
            };
            
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                contentParts.Add(new { type = "text", text = prompt });
            }
            
            messages.Add(new { role = "user", content = contentParts.ToArray() });
        }
        else
        {
            // Text-only message
            messages.Add(new { role = "user", content = prompt });
        }

        // Build request body
        object requestBody;
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            if (config.Tools != null && config.Tools.Count > 0)
            {
                requestBody = new
                {
                    model = model,
                    max_tokens = maxTokens,
                    system = systemPrompt,
                    messages = messages.ToArray(),
                    tools = config.Tools
                };
            }
            else
            {
                requestBody = new
                {
                    model = model,
                    max_tokens = maxTokens,
                    system = systemPrompt,
                    messages = messages.ToArray()
                };
            }
        }
        else
        {
            if (config.Tools != null && config.Tools.Count > 0)
            {
                requestBody = new
                {
                    model = model,
                    max_tokens = maxTokens,
                    messages = messages.ToArray(),
                    tools = config.Tools
                };
            }
            else
            {
                requestBody = new
                {
                    model = model,
                    max_tokens = maxTokens,
                    messages = messages.ToArray()
                };
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        // Log request details
        var requestJson = JsonSerializer.Serialize(new
        {
            Model = model,
            MaxTokens = maxTokens,
            PromptLength = prompt.Length,
            PromptPreview = prompt.Length > 500 ? prompt.Substring(0, 500) + "..." : prompt
        }, new JsonSerializerOptions { WriteIndented = true });
        Log(node, NodeLogLevel.Info, $"Sending request to Anthropic ({model})", requestJson);

        try
        {
            var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log(node, NodeLogLevel.Error, $"Anthropic API returned {(int)response.StatusCode}: {response.ReasonPhrase}", responseContent);
                return new NodeExecutionResult { Success = false, ErrorMessage = $"Anthropic API error ({(int)response.StatusCode}): {responseContent}" };
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            
            // Extract response content - Anthropic returns an array of content blocks
            var contentBlocks = jsonResponse.GetProperty("content");
            var aiResponse = "";
            var toolCalls = new List<object>();
            
            foreach (var block in contentBlocks.EnumerateArray())
            {
                var blockType = block.GetProperty("type").GetString();
                if (blockType == "text")
                {
                    aiResponse += block.GetProperty("text").GetString() ?? "";
                }
                else if (blockType == "tool_use")
                {
                    toolCalls.Add(new
                    {
                        id = block.GetProperty("id").GetString(),
                        name = block.GetProperty("name").GetString(),
                        input = block.GetProperty("input").ToString()
                    });
                }
            }

            var stopReason = jsonResponse.GetProperty("stop_reason").GetString() ?? "";

            // Calculate cost
            string? usageInfo = null;
            if (jsonResponse.TryGetProperty("usage", out var usage))
            {
                usageInfo = JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true });

                if (usage.TryGetProperty("input_tokens", out var inputTokensElement) &&
                    usage.TryGetProperty("output_tokens", out var outputTokensElement))
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

            // Log success with cost
            var detail = JsonSerializer.Serialize(new
            {
                Model = model,
                ResponseLength = aiResponse.Length,
                StopReason = stopReason,
                ToolCallCount = toolCalls.Count,
                Usage = usageInfo,
                RunCost = lastRunCost,
                TotalCost = config.Cost
            }, new JsonSerializerOptions { WriteIndented = true });

            var costString = lastRunCost.HasValue ? $" (Cost: ${lastRunCost.Value:F5})" : "";
            Log(node, NodeLogLevel.Info, $"Received response from Anthropic ({aiResponse.Length} chars){costString}", detail);

            // If conversation mode is enabled, cache the conversation history
            string? conversationHistoryJson = null;
            if (config.ConversationMode)
            {
                // Initialize history if null
                config.ConversationHistory ??= new List<AnthropicMessage>();
                
                // Append current user message (text only, not image content)
                config.ConversationHistory.Add(new AnthropicMessage { Role = "user", Content = prompt });
                
                // Append assistant response
                config.ConversationHistory.Add(new AnthropicMessage { Role = "assistant", Content = aiResponse });
                
                // Serialize for output
                conversationHistoryJson = JsonSerializer.Serialize(config.ConversationHistory);
                
                // Save updated config with history
                config.Prompt = originalPrompt;
                config.SystemPrompt = originalSystemPrompt;
                node.Configuration = JsonSerializer.Serialize(config);
                _executionManager.NotifyConfigurationUpdated(node.Id, node.Configuration);
            }

            return new NodeExecutionResult
            {
                Success = true,
                OutputData = new Dictionary<string, object?>
                {
                    { "AIResponse", aiResponse },
                    { "ModelUsed", model },
                    { "StopReason", stopReason },
                    { "ToolCalls", toolCalls.Count > 0 ? JsonSerializer.Serialize(toolCalls) : null },
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
            Log(node, NodeLogLevel.Error, $"Anthropic API error: {ex.Message}");
            return new NodeExecutionResult { Success = false, ErrorMessage = $"Anthropic API error: {ex.Message}" };
        }
    }

    public override List<string> GetOutputParameters()
    {
        return new List<string> { "AIResponse", "ModelUsed", "StopReason", "ToolCalls", "ConversationHistory" };
    }

    private static double GetInputRate(string model)
    {
        // Pricing per million tokens (from Anthropic pricing table)
        return model switch
        {
            // Claude 4.5
            "claude-opus-4-5-20250514" => 5.00,
            "claude-sonnet-4-5-20250514" => 3.00,
            "claude-haiku-4-5-20250514" => 1.00,
            // Claude 4
            "claude-opus-4-0520" or "claude-opus-4-20250514" => 15.00,
            "claude-sonnet-4-0520" or "claude-sonnet-4-20250514" => 3.00,
            // Claude 3.7
            "claude-3-7-sonnet-20250219" => 3.00,
            // Claude 3.5
            "claude-3-5-sonnet-20241022" or "claude-3-5-sonnet-20240620" => 3.00,
            "claude-3-5-haiku-20241022" => 0.80,
            // Claude 3
            "claude-3-opus-20240229" => 15.00,
            "claude-3-sonnet-20240229" => 3.00,
            "claude-3-haiku-20240307" => 0.25,
            // Legacy
            "claude-2.1" or "claude-2.0" => 8.00,
            _ => 3.00 // Default to Sonnet pricing
        };
    }

    private static double GetOutputRate(string model)
    {
        return model switch
        {
            // Claude 4.5
            "claude-opus-4-5-20250514" => 25.00,
            "claude-sonnet-4-5-20250514" => 15.00,
            "claude-haiku-4-5-20250514" => 5.00,
            // Claude 4
            "claude-opus-4-0520" or "claude-opus-4-20250514" => 75.00,
            "claude-sonnet-4-0520" or "claude-sonnet-4-20250514" => 15.00,
            // Claude 3.7
            "claude-3-7-sonnet-20250219" => 15.00,
            // Claude 3.5
            "claude-3-5-sonnet-20241022" or "claude-3-5-sonnet-20240620" => 15.00,
            "claude-3-5-haiku-20241022" => 4.00,
            // Claude 3
            "claude-3-opus-20240229" => 75.00,
            "claude-3-sonnet-20240229" => 15.00,
            "claude-3-haiku-20240307" => 1.25,
            // Legacy
            "claude-2.1" or "claude-2.0" => 24.00,
            _ => 15.00 // Default to Sonnet pricing
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

public class AnthropicConfig
{
    public string? Model { get; set; } = "claude-sonnet-4-20250514";
    public int MaxTokens { get; set; } = 1024;
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public double Cost { get; set; } = 0;
    public long InputTokens { get; set; } = 0;
    public long OutputTokens { get; set; } = 0;
    
    // For conversational turns
    public List<AnthropicMessage>? Messages { get; set; }
    
    // Conversation mode - caches history within node
    public bool ConversationMode { get; set; } = false;
    public List<AnthropicMessage>? ConversationHistory { get; set; }
    
    // For tool use
    public List<object>? Tools { get; set; }
    
    // For image analysis
    public string? ImageBase64 { get; set; }
    public string? MediaType { get; set; } = "image/png";
}

public class AnthropicMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}
