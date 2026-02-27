using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using S2GPulseWeb.Web.Data;

namespace S2GPulseWeb.Web.Logic;

public class WorkflowAssistantService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly UserSecretService _secretService;
    private readonly NodeKnowledgeService _nodeKnowledge;
    private readonly CopilotConnectorService _copilotService;
    private readonly List<ChatConversation> _conversations = new();
    private ChatConversation? _currentConversation;
    
    // Provider API endpoints
    private static readonly Dictionary<string, string> ProviderEndpoints = new()
    {
        ["OpenAI"] = "https://api.openai.com/v1/chat/completions",
        ["DeepSeek"] = "https://api.deepseek.com/chat/completions",
        ["Anthropic"] = "https://api.anthropic.com/v1/messages",
        ["Gemini"] = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
        ["Mistral"] = "https://api.mistral.ai/v1/chat/completions",
        ["Groq"] = "https://api.groq.com/openai/v1/chat/completions"
    };
    
    // Provider API key secret names
    public static readonly Dictionary<string, string> ProviderSecretKeys = new()
    {
        ["OpenAI"] = "OpenAI_ApiKey",
        ["DeepSeek"] = "DeepSeek_ApiKey",
        ["Anthropic"] = "Anthropic_ApiKey",
        ["Gemini"] = "Gemini_ApiKey",
        ["Mistral"] = "Mistral_ApiKey",
        ["Groq"] = "Groq_ApiKey"
    };
    
    // Available models per provider (first is default)
    public static readonly Dictionary<string, List<string>> ProviderModels = new()
    {
        ["OpenAI"] = new() { "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-4", "gpt-3.5-turbo", "o1", "o1-mini", "o3-mini" },
        ["DeepSeek"] = new() { "deepseek-chat", "deepseek-reasoner" },
        ["Anthropic"] = new() { "claude-sonnet-4-20250514", "claude-3-5-sonnet-20241022", "claude-3-5-haiku-20241022", "claude-3-opus-20240229" },
        ["Gemini"] = new() { "gemini-2.0-flash", "gemini-2.0-flash-lite", "gemini-1.5-pro", "gemini-1.5-flash" },
        ["Mistral"] = new() { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest", "codestral-latest" },
        ["Groq"] = new() { "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "mixtral-8x7b-32768", "gemma2-9b-it" },
        ["Copilot"] = new() { "gpt-4.1", "gpt-4o", "gpt-5-mini", "claude-sonnet-4", "claude-sonnet-4.5", "claude-opus-4.5", "gemini-2.5-pro", "gemini-3-flash-preview" }
    };
    
    /// <summary>
    /// Gets available models for a provider.
    /// </summary>
    public static List<string> GetModelsForProvider(string provider)
    {
        return ProviderModels.TryGetValue(provider, out var models) ? models : new List<string>();
    }
    
    /// <summary>
    /// Gets the default model for a provider.
    /// </summary>
    public static string GetDefaultModel(string provider)
    {
        return ProviderModels.TryGetValue(provider, out var models) && models.Any() 
            ? models[0] 
            : "gpt-4o";
    }

    public WorkflowAssistantService(
        HttpClient httpClient, 
        IConfiguration configuration, 
        UserSecretService secretService,
        NodeKnowledgeService nodeKnowledge,
        CopilotConnectorService copilotService)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _secretService = secretService;
        _nodeKnowledge = nodeKnowledge;
        _copilotService = copilotService;
    }

    public ChatConversation CurrentConversation
    {
        get
        {
            if (_currentConversation == null)
            {
                StartNewConversation();
            }
            return _currentConversation!;
        }
    }

    public void StartNewConversation()
    {
        _currentConversation = new ChatConversation();
        _conversations.Add(_currentConversation);
    }

    /// <summary>
    /// Sends a message to the selected AI provider
    /// </summary>
    public async Task<WorkflowAssistantResult> SendMessageAsync(
        string userMessage, 
        string userId, 
        string provider = "OpenAI",
        string mode = "Ask",
        string? model = null,
        string? currentWorkflowJson = null,
        string temperatureMode = "Focused",
        string? tierConstraintsJson = null,
        Guid? copilotConnectionId = null)
    {
        var userMsg = new ChatMessage { Role = "user", Content = userMessage };
        CurrentConversation.Messages.Add(userMsg);

        // Calculate temperature: Focused = 0.2, Creative = 0.8
        var temperature = temperatureMode == "Creative" ? 0.8f : 0.2f;
        
        var prompt = BuildPrompt(userMessage, currentWorkflowJson, tierConstraintsJson);
        
        try
        {
            // Special handling for Copilot provider (uses OAuth connection instead of API key)
            if (provider == "Copilot")
            {
                if (!copilotConnectionId.HasValue)
                {
                    return new WorkflowAssistantResult 
                    { 
                        Success = false, 
                        Message = "No Copilot connection is designated for AI Builder. Go to Settings → Connections to set one up.",
                        ErrorMessage = "Copilot connection not configured." 
                    };
                }
                
                var selectedModel = model ?? GetDefaultModel(provider);
                var content = await SendToCopilotAsync(copilotConnectionId.Value, prompt, mode, selectedModel, temperature);
                
                var result = ParseResponse(content);
                var displayMessage = result.Message ?? content;
                var assistantMsg = new ChatMessage { Role = "assistant", Content = displayMessage };
                CurrentConversation.Messages.Add(assistantMsg);
                return result;
            }
            
            // Standard API key-based providers
            var secretKey = ProviderSecretKeys.GetValueOrDefault(provider, "OpenAI_ApiKey");
            var apiKey = await _secretService.GetSecretAsync(userId, secretKey);

            if (string.IsNullOrEmpty(apiKey))
            {
                return new WorkflowAssistantResult 
                { 
                    Success = false, 
                    Message = $"Your {provider} API Key is not configured. Please go to Settings to add it.",
                    ErrorMessage = "API Key not configured." 
                };
            }

            // Use specified model or default for provider
            var selectedModelStandard = model ?? GetDefaultModel(provider);
            
            // Route to appropriate provider
            var contentStandard = provider switch
            {
                "Anthropic" => await SendToAnthropicAsync(apiKey, prompt, mode, selectedModelStandard, temperature),
                _ => await SendToOpenAICompatibleAsync(provider, apiKey, prompt, mode, selectedModelStandard, temperature)
            };

            // Parse the JSON response first
            var resultStandard = ParseResponse(contentStandard);
            
            // Store only the message text in conversation (not raw JSON)
            var displayMessageStandard = resultStandard.Message ?? contentStandard;
            var assistantMsgStandard = new ChatMessage { Role = "assistant", Content = displayMessageStandard };
            CurrentConversation.Messages.Add(assistantMsgStandard);

            return resultStandard;
        }
        catch (Exception ex)
        {
            return new WorkflowAssistantResult { Success = false, ErrorMessage = ex.Message };
        }
    }
    
    /// <summary>
    /// Sends request to OpenAI-compatible APIs (OpenAI, DeepSeek, Gemini, Mistral, Groq)
    /// </summary>
    private async Task<string> SendToOpenAICompatibleAsync(string provider, string apiKey, string prompt, string mode, string model, float temperature)
    {
        var endpoint = ProviderEndpoints.GetValueOrDefault(provider, ProviderEndpoints["OpenAI"]);
        
        var systemPrompt = GetSystemPrompt(mode);
        
        var requestBody = new
        {
            model = model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            },
            temperature = temperature,
            response_format = new { type = "json_object" }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>();
        return jsonResponse.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
    
    /// <summary>
    /// Sends request to GitHub Copilot API using OAuth connection
    /// </summary>
    private async Task<string> SendToCopilotAsync(Guid connectionId, string prompt, string mode, string model, float temperature)
    {
        // Get valid token (auto-refreshes if needed)
        var token = await _copilotService.GetValidCopilotTokenAsync(connectionId);
        if (string.IsNullOrEmpty(token))
        {
            throw new Exception("Failed to get valid Copilot token. Please reconnect your GitHub Copilot account.");
        }
        
        var systemPrompt = GetSystemPrompt(mode);
        
        var request = new CopilotChatRequest
        {
            Model = model,
            Messages = new List<CopilotChatMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = prompt }
            },
            Temperature = temperature,
            ResponseFormat = new CopilotResponseFormat { Type = "json_object" }
        };
        
        var response = await _copilotService.ChatCompletionsAsync(token, request);
        
        if (!string.IsNullOrEmpty(response?.Error))
        {
            throw new Exception($"Copilot API error: {response.Error}");
        }
        
        // Extract content from response
        var content = response?.Choices?.FirstOrDefault()?.Message?.Content;
        return content ?? "";
    }
    
    
    /// <summary>
    /// Sends request to Anthropic Claude API (different format)
    /// </summary>
    private async Task<string> SendToAnthropicAsync(string apiKey, string prompt, string mode, string model, float temperature)
    {
        var systemPrompt = GetSystemPrompt(mode);
        
        var requestBody = new
        {
            model = model,
            max_tokens = 4096,
            temperature = temperature,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ProviderEndpoints["Anthropic"])
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>();
        return jsonResponse.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private string BuildPrompt(string userMessage, string? currentWorkflowJson, string? tierConstraintsJson = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"User Request: {userMessage}");
        if (!string.IsNullOrEmpty(currentWorkflowJson))
        {
            sb.AppendLine($"Selected Nodes Context: {currentWorkflowJson}");
        }
        if (!string.IsNullOrEmpty(tierConstraintsJson))
        {
            sb.AppendLine($"User Tier Constraints: {tierConstraintsJson}");
        }
        return sb.ToString();
    }

    private string GetSystemPrompt(string mode)
    {
        var nodeKnowledge = _nodeKnowledge.GetFullNodeCatalogDocumentation();
        
        var modeInstructions = mode switch
        {
            "Ask" => "You are answering questions about workflows and nodes. Provide helpful explanations ONLY. **DO NOT make any changes to the workflow in Ask mode.** Your 'actions' array MUST be empty. If the user asks you to make changes, politely explain they should switch to Edit or Build mode.",
            "Build" => "You are creating a new workflow from scratch. Generate all necessary nodes and connections.",
            "Edit" => "You are modifying an existing workflow. Reference nodes by their names using {{NodeName}} syntax.",
            _ => "You are helping with workflow operations."
        };
        
        return $@"You are an AI Workflow Assistant for S2G-Pulse-Web. {modeInstructions}

## Response Format
You must respond in JSON format with two fields:
1. 'message': A friendly explanation in **Markdown format**. Use:
   - **Bold** for emphasis
   - `code` for node names, properties, and values
   - Code blocks for longer examples
   - Bullet lists for multiple items
2. 'actions': A list of actions to perform (can be empty for Ask mode).

## Available Actions
- {{ ""action"": ""create_node"", ""parameters"": {{ ""type"": ""NodeType"", ""name"": ""NodeName"", ""x"": 100, ""y"": 100, ""width"": 200, ""height"": 100, ""properties"": {{ ... }} }} }}
- {{ ""action"": ""connect_nodes"", ""parameters"": {{ ""sourceNodeName"": ""Source"", ""targetNodeName"": ""Target"", ""label"": ""success"" }} }}
- {{ ""action"": ""set_property"", ""parameters"": {{ ""nodeName"": ""Name"", ""propertyName"": ""Prop"", ""value"": ""Val"" }} }}
- {{ ""action"": ""set_surface_fields"", ""parameters"": {{ ""nodeName"": ""MyNode"", ""surfaceFields"": [""Label: {{{{MyNode.OutputField}}}}""] }} }}
- {{ ""action"": ""move_node"", ""parameters"": {{ ""nodeName"": ""NodeName"", ""x"": 300, ""y"": 400 }} }}
- {{ ""action"": ""resize_node"", ""parameters"": {{ ""nodeName"": ""NodeName"", ""width"": 250, ""height"": 120 }} }}
- {{ ""action"": ""clear_workflow"", ""parameters"": {{}} }}

## Node Sizing and Spacing
**IMPORTANT: Always specify proper node dimensions and spacing!**
- Default node size: width=300, height=200
- Minimum spacing between nodes: 150px vertically, 250px horizontally
- Vertical layouts: increment Y by 150-180px per node
- Horizontal layouts: increment X by 280-320px per node
- Larger nodes (with many surface fields): width=250, height=120-150

## User Tier Constraints
**CRITICAL: The user's tier limits may be provided in the prompt. Respect these limits!**
- Check 'remainingNodes' before creating new nodes - do NOT exceed this limit
- If 'canUseScheduling' is false, do NOT create Scheduler nodes
- If limit would be exceeded, explain the limitation and suggest alternatives
- Example: If remainingNodes=2 and user asks for 5 nodes, create max 2 and explain

## Surface Fields
Surface fields are display labels shown directly on nodes. They show placeholder values at a glance.
**IMPORTANT: Placeholders MUST include the node name as prefix to work!**
- Format: ""Label: {{{{NodeName.PropertyKey}}}}""
- The node name prefix is REQUIRED - without it, the value won't display
- Example for node named ""Query"": ""Result: {{{{Query.RowCount}}}}""
- Example for node named ""Response"": ""Status: {{{{Response.StatusCode}}}}""

## Node Reference
{nodeKnowledge}

## Example Response
{{
    ""message"": ""I'll create a simple **API endpoint** that queries a database.\n\n### Nodes Created:\n- `Listener` - HTTP trigger\n- `Query` - SQL Server query"",
    ""actions"": [
        {{ ""action"": ""create_node"", ""parameters"": {{ ""type"": ""HttpListener"", ""name"": ""Listener"", ""x"": 100, ""y"": 100 }} }},
        {{ ""action"": ""create_node"", ""parameters"": {{ ""type"": ""SqlServer"", ""name"": ""Query"", ""x"": 100, ""y"": 220 }} }},
        {{ ""action"": ""connect_nodes"", ""parameters"": {{ ""sourceNodeName"": ""Listener"", ""targetNodeName"": ""Query"" }} }}
    ]
}}";
    }

    private WorkflowAssistantResult ParseResponse(string content)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            
            // Try direct parsing first
            var result = JsonSerializer.Deserialize<WorkflowAssistantResult>(content, options);
            if (result != null)
            {
                result.Success = true;
                return result;
            }
        }
        catch 
        {
            // If direct parsing fails, try to extract JSON from the content
            try
            {
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var result = JsonSerializer.Deserialize<WorkflowAssistantResult>(jsonContent, options);
                    if (result != null)
                    {
                        result.Success = true;
                        return result;
                    }
                }
            }
            catch { }
        }

        return new WorkflowAssistantResult { Success = true, Message = content };
    }
}
