using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic.Nodes;

/// <summary>
/// Local LLM Node — connects to self-hosted OpenAI-compatible LLM servers
/// (vLLM, Ollama, LM Studio, text-generation-webui, etc.).
/// Supports thinking/reasoning mode for Qwen3 and similar models.
/// </summary>
public class LocalLlmNode : BaseNodeExecutor
{
    private readonly HttpClient _httpClient;

    public LocalLlmNode(HttpClient httpClient, NodeExecutionManager executionManager)
        : base(executionManager)
    {
        _httpClient = httpClient;
    }

    public override string NodeType => "LocalLlm";

    protected override async Task<NodeExecutionResult> InternalExecuteAsync(
        WorkflowNode node,
        Dictionary<string, object?> inputData,
        string userId)
    {
        var config = JsonSerializer.Deserialize<LocalLlmConfig>(node.Configuration ?? "{}") ?? new LocalLlmConfig();

        // Preserve original prompt values for restoration after placeholder resolution
        var originalPrompt = config.Prompt;
        var originalSystemPrompt = config.SystemPrompt;

        // Validate required fields
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Base URL is required. Configure the URL of your local LLM server (e.g., http://host:port/v1)."
            };
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = "Model name is required. Use the 'List Models' button in the diagnostics panel to discover available models."
            };
        }

        // Resolve placeholders in prompts
        var prompt = ReplacePlaceholders(config.Prompt, inputData);
        var systemPrompt = ReplacePlaceholders(config.SystemPrompt ?? "", inputData);

        // ═══════════════════════════════════════════════════════════════════════════
        // ORCHESTRATOR CONTEXT HANDLING
        // ═══════════════════════════════════════════════════════════════════════════

        if (inputData.TryGetValue("_OrchestratorSystemPromptOverride", out var sysOverride) &&
            !string.IsNullOrEmpty(sysOverride?.ToString()))
        {
            systemPrompt = sysOverride.ToString()!;
            Log(node, NodeLogLevel.Info, "Using orchestrator system prompt override");
        }

        if (inputData.TryGetValue("_OrchestratorPromptAppend", out var promptAppend) &&
            !string.IsNullOrEmpty(promptAppend?.ToString()))
        {
            prompt = $"{prompt}\n\n[Orchestrator Feedback]:\n{promptAppend}";
            Log(node, NodeLogLevel.Info, $"Appended orchestrator feedback: {Truncate(promptAppend?.ToString(), 100)}");
        }

        if (inputData.TryGetValue("_OrchestratorIteration", out var iteration))
        {
            var roleName = inputData.GetValueOrDefault("_OrchestratorRoleName")?.ToString() ?? "Agent";
            Log(node, NodeLogLevel.Info, $"Orchestrated execution: Role={roleName}, Iteration={iteration}");
        }

        // ═══════════════════════════════════════════════════════════════════════════

        // Build messages array
        List<Dictionary<string, object?>> messages;

        if (!string.IsNullOrWhiteSpace(config.Messages))
        {
            // Raw JSON messages override
            try
            {
                messages = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                    ReplacePlaceholders(config.Messages, inputData)) ?? new();
                Log(node, NodeLogLevel.Info, $"Using raw messages JSON ({messages.Count} messages)");
            }
            catch (Exception ex)
            {
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Invalid Messages JSON: {ex.Message}"
                };
            }
        }
        else
        {
            messages = new List<Dictionary<string, object?>>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                messages.Add(new Dictionary<string, object?> { { "role", "system" }, { "content", systemPrompt } });
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new NodeExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Prompt is required. Enter a prompt or provide upstream placeholders."
                };
            }

            messages.Add(new Dictionary<string, object?> { { "role", "user" }, { "content", prompt } });
        }

        // Build request body
        var requestBody = new Dictionary<string, object?>
        {
            { "model", config.Model },
            { "messages", messages },
            { "max_tokens", config.MaxTokens },
            { "temperature", config.Temperature },
            { "stream", false }
        };

        // Add thinking mode support (vLLM uses chat_template_kwargs)
        if (config.EnableThinking)
        {
            requestBody["chat_template_kwargs"] = new Dictionary<string, object>
            {
                { "enable_thinking", true }
            };
            Log(node, NodeLogLevel.Info, "Thinking mode enabled");
        }

        // Construct API URL
        var baseUrl = config.BaseUrl.TrimEnd('/');
        var apiUrl = baseUrl.EndsWith("/v1")
            ? $"{baseUrl}/chat/completions"
            : baseUrl.Contains("/chat/completions")
                ? baseUrl
                : $"{baseUrl}/v1/chat/completions";

        Log(node, NodeLogLevel.Info, $"Calling {config.Model} at {apiUrl}");

        // Send request
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = JsonContent.Create(requestBody)
        };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        var timeoutMs = (config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 300) * 1000;
        using var cts = new CancellationTokenSource(timeoutMs);

        HttpResponseMessage response;
        string responseContent;
        try
        {
            response = await _httpClient.SendAsync(request, cts.Token);
            responseContent = await response.Content.ReadAsStringAsync();
        }
        catch (TaskCanceledException)
        {
            Log(node, NodeLogLevel.Error, $"Request timed out after {config.TimeoutSeconds}s");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"Request timed out after {config.TimeoutSeconds} seconds.",
            };
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Request failed: {ex.Message}");
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"Connection failed: {ex.Message}",
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            Log(node, NodeLogLevel.Error, $"API error: {(int)response.StatusCode}", responseContent);
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"LLM API error ({(int)response.StatusCode}): {Truncate(responseContent, 500)}",
            };
        }

        // Parse response
        JsonElement jsonResponse;
        try
        {
            jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
        }
        catch (Exception ex)
        {
            Log(node, NodeLogLevel.Error, $"Failed to parse response: {ex.Message}", responseContent);
            return new NodeExecutionResult
            {
                Success = false,
                ErrorMessage = $"Invalid JSON response: {ex.Message}",
            };
        }

        // Extract response content
        var choice = jsonResponse.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var rawContent = message.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
        var finishReason = choice.TryGetProperty("finish_reason", out var frEl) ? frEl.GetString() ?? "" : "";
        var modelUsed = jsonResponse.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? config.Model : config.Model;

        // Parse thinking tags if present
        string aiResponse;
        string thinkingContent = "";

        if (config.EnableThinking || rawContent.Contains("<think>"))
        {
            var thinkMatch = Regex.Match(rawContent, @"<think>(.*?)</think>", RegexOptions.Singleline);
            if (thinkMatch.Success)
            {
                thinkingContent = thinkMatch.Groups[1].Value.Trim();
                aiResponse = Regex.Replace(rawContent, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
                Log(node, NodeLogLevel.Info, $"Extracted thinking content ({thinkingContent.Length} chars)");
            }
            else
            {
                aiResponse = rawContent;
            }
        }
        else
        {
            aiResponse = rawContent;
        }

        // Track token usage
        long promptTokens = 0, completionTokens = 0;
        if (jsonResponse.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt64() : 0;
            completionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt64() : 0;
        }

        // Update cumulative token stats and restore original prompts
        config.InputTokens += promptTokens;
        config.OutputTokens += completionTokens;
        config.Prompt = originalPrompt;
        config.SystemPrompt = originalSystemPrompt;
        node.Configuration = JsonSerializer.Serialize(config);
        _executionManager?.NotifyConfigurationUpdated(node.Id, node.Configuration);

        Log(node, NodeLogLevel.Info,
            $"Complete. Model: {modelUsed}, Tokens: {promptTokens} in / {completionTokens} out, Finish: {finishReason}");
        Log(node, NodeLogLevel.Info, "Response", Truncate(aiResponse, 500));

        return new NodeExecutionResult
        {
            Success = true,
            OutputData = new Dictionary<string, object?>
            {
                { "Response", aiResponse },
                { "ThinkingContent", thinkingContent },
                { "FullMessage", rawContent },
                { "Model", modelUsed },
                { "PromptTokens", promptTokens },
                { "CompletionTokens", completionTokens },
                { "FinishReason", finishReason }
            }
        };
    }

    public override List<string> GetOutputParameters() => new()
    {
        "Response", "ThinkingContent", "FullMessage", "Model", "PromptTokens", "CompletionTokens", "FinishReason"
    };

    private string ReplacePlaceholders(string template, Dictionary<string, object?> data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        foreach (var kvp in data)
        {
            if (kvp.Key.StartsWith("_")) continue; // Skip internal keys
            template = template.Replace($"{{{{{kvp.Key}}}}}", kvp.Value?.ToString() ?? "");
        }
        return template;
    }

    private static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }
}

/// <summary>
/// Configuration model for the Local LLM node.
/// </summary>
public class LocalLlmConfig
{
    /// <summary>Base URL of the LLM server (e.g., http://192.168.1.89:8000/v1)</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Optional API key for servers requiring authentication</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model name (e.g., huihui, llama-3, qwen3)</summary>
    public string Model { get; set; } = "";

    /// <summary>System prompt to set AI behavior</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>User prompt (supports {{placeholders}})</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Raw JSON messages array (overrides SystemPrompt + Prompt if set)</summary>
    public string? Messages { get; set; }

    /// <summary>Maximum tokens to generate</summary>
    public int MaxTokens { get; set; } = 2048;

    /// <summary>Sampling temperature (0.0 - 2.0)</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Enable thinking/reasoning mode (for Qwen3 and similar models)</summary>
    public bool EnableThinking { get; set; } = false;

    /// <summary>Request timeout in seconds</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Cumulative input tokens tracked across executions</summary>
    public long InputTokens { get; set; } = 0;

    /// <summary>Cumulative output tokens tracked across executions</summary>
    public long OutputTokens { get; set; } = 0;
}
