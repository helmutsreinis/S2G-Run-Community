using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web.Data;
using S2GPulseWeb.Web.Logic;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// REST API for AI-powered workflow generation and sample retrieval.
/// </summary>
[ApiController]
[Route("api/v1/ai")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class AiAssistantApiController : ControllerBase
{
    private readonly WorkflowAssistantService _assistantService;
    private readonly WorkflowApiService _workflowApiService;
    private readonly UserSecretService _secretService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IWebHostEnvironment _env;

    public AiAssistantApiController(
        WorkflowAssistantService assistantService,
        WorkflowApiService workflowApiService,
        UserSecretService secretService,
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IWebHostEnvironment env)
    {
        _assistantService = assistantService;
        _workflowApiService = workflowApiService;
        _secretService = secretService;
        _dbContextFactory = dbContextFactory;
        _env = env;
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>List all available AI providers with their models and configuration status.</summary>
    [HttpGet("providers")]
    public async Task<IActionResult> ListProviders()
    {
        var userId = GetUserId();

        // Check which providers have API keys configured
        var secrets = await _secretService.GetUserSecretsAsync(userId);
        var configuredSecretNames = secrets.Select(s => s.Name).ToHashSet();

        // Check Copilot: user has at least one OAuth connection for GitHub Copilot
        bool hasCopilot = false;
        await using var ctx = await _dbContextFactory.CreateDbContextAsync();
        hasCopilot = await ctx.OAuthConnections
            .AnyAsync(c => c.UserId == userId && c.Provider == "GitHubCopilot");

        var providers = WorkflowAssistantService.ProviderModels
            .Select(kv => new
            {
                provider = kv.Key,
                models = kv.Value,
                defaultModel = kv.Value.FirstOrDefault(),
                isConfigured = kv.Key == "Copilot"
                    ? hasCopilot
                    : WorkflowAssistantService.ProviderSecretKeys.TryGetValue(kv.Key, out var secretName)
                        && configuredSecretNames.Contains(secretName),
                authType = kv.Key == "Copilot" ? "oauth" : "api_key"
            })
            .OrderByDescending(p => p.isConfigured)
            .ThenBy(p => p.provider)
            .ToList();

        return Ok(providers);
    }

    /// <summary>Generate a workflow using AI from a natural language prompt. The workflow is created and persisted.</summary>
    [HttpPost("generate")]
    public async Task<IActionResult> GenerateWorkflow([FromBody] AiGenerateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { error = "Prompt is required." });

        var userId = GetUserId();

        // Start a fresh conversation for each API generation
        _assistantService.StartNewConversation();

        // Auto-resolve Copilot connection if provider is Copilot
        Guid? copilotConnectionId = null;
        var provider = request.Provider ?? "OpenAI";
        if (provider == "Copilot")
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            
            var pref = await context.UserPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId);
            
            if (pref?.AiBuilderCopilotConnectionId != null)
            {
                copilotConnectionId = pref.AiBuilderCopilotConnectionId;
            }
            else
            {
                var conn = await context.OAuthConnections
                    .Where(c => c.UserId == userId && c.Provider == "GitHubCopilot")
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync();
                
                if (conn == null)
                    return BadRequest(new { error = "No GitHub Copilot connection found. Connect via Settings → Connections first." });
                
                copilotConnectionId = conn.Id;
            }
        }

        var result = await _assistantService.SendMessageAsync(
            userMessage: request.Prompt,
            userId: userId,
            provider: provider,
            mode: "Build",
            model: request.Model,
            currentWorkflowJson: null,
            temperatureMode: request.Temperature ?? "Focused",
            copilotConnectionId: copilotConnectionId
        );

        if (!result.Success)
        {
            return BadRequest(new
            {
                error = result.ErrorMessage ?? "AI generation failed.",
                message = result.Message
            });
        }

        // Convert AI actions into a real workflow
        var nodes = new List<WorkflowNodeDto>();
        var connections = new List<WorkflowConnectionDto>();
        var surfaceFieldsMap = new Dictionary<string, List<string>>();

        if (result.Actions != null)
        {
            foreach (var action in result.Actions)
            {
                var p = action.Parameters;
                switch (action.Action)
                {
                    case "create_node":
                        var node = new WorkflowNodeDto
                        {
                            NodeType = GetParam(p, "type"),
                            Name = GetParam(p, "name"),
                        };
                        if (p.TryGetValue("x", out var xVal) && double.TryParse(xVal?.ToString(), out var x)) node.X = x;
                        if (p.TryGetValue("y", out var yVal) && double.TryParse(yVal?.ToString(), out var y)) node.Y = y;
                        if (p.TryGetValue("width", out var wVal) && double.TryParse(wVal?.ToString(), out var w)) node.Width = w;
                        if (p.TryGetValue("height", out var hVal) && double.TryParse(hVal?.ToString(), out var h)) node.Height = h;
                        
                        // Extract configuration from properties
                        if (p.TryGetValue("properties", out var props) && props != null)
                        {
                            node.Configuration = props is JsonElement je 
                                ? je.GetRawText() 
                                : JsonSerializer.Serialize(props);
                        }
                        nodes.Add(node);
                        break;

                    case "connect_nodes":
                        connections.Add(new WorkflowConnectionDto
                        {
                            SourceName = GetParam(p, "sourceNodeName"),
                            TargetName = GetParam(p, "targetNodeName"),
                            Label = p.TryGetValue("label", out var lbl) ? lbl?.ToString() : null
                        });
                        break;

                    case "set_surface_fields":
                        var sfNodeName = GetParam(p, "nodeName");
                        if (p.TryGetValue("surfaceFields", out var sfVal) && sfVal != null)
                        {
                            var fields = sfVal is JsonElement sfJson
                                ? sfJson.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                                : new List<string>();
                            surfaceFieldsMap[sfNodeName] = fields;
                        }
                        break;
                }
            }
        }

        // Apply surface fields to matching nodes
        foreach (var node in nodes)
        {
            if (surfaceFieldsMap.TryGetValue(node.Name, out var sf))
                node.SurfaceFields = sf;
        }

        if (!nodes.Any())
        {
            return Ok(new
            {
                message = result.Message,
                warning = "AI did not produce any nodes. Try a more specific prompt.",
                success = true
            });
        }

        // Generate a workflow name from the prompt
        var workflowName = request.Name 
            ?? (request.Prompt.Length > 60 ? request.Prompt[..60] + "…" : request.Prompt);

        var createRequest = new WorkflowCreateRequest
        {
            Name = workflowName,
            Description = $"AI-generated ({provider}/{request.Model ?? "default"})",
            Nodes = nodes,
            Connections = connections
        };

        var (workflow, invalidTypes) = await _workflowApiService.CreateWorkflowAsync(userId, createRequest);

        if (invalidTypes != null)
            return BadRequest(new { error = $"AI generated unknown node type(s): {string.Join(", ", invalidTypes)}", success = false });

        return CreatedAtAction(null, new { id = workflow!.Id }, new
        {
            message = result.Message,
            workflow,
            success = true
        });
    }


    /// <summary>List available workflow samples.</summary>
    [HttpGet("samples")]
    public IActionResult ListSamples()
    {
        var samplesDir = GetSamplesDirectory();
        if (!Directory.Exists(samplesDir))
            return Ok(Array.Empty<object>());

        var files = Directory.GetFiles(samplesDir, "*.json");
        var samples = files.Select(f =>
        {
            var fileName = Path.GetFileNameWithoutExtension(f);
            try
            {
                var json = System.IO.File.ReadAllText(f);
                using var doc = JsonDocument.Parse(json);
                var name = doc.RootElement.TryGetProperty("Name", out var nameProp)
                    ? nameProp.GetString() : fileName;

                var nodeCount = doc.RootElement.TryGetProperty("Nodes", out var nodes)
                    ? nodes.GetArrayLength() : 0;
                var connCount = doc.RootElement.TryGetProperty("Connections", out var conns)
                    ? conns.GetArrayLength() : 0;

                return new
                {
                    fileName = Path.GetFileName(f),
                    name,
                    nodeCount,
                    connectionCount = connCount
                };
            }
            catch
            {
                return new { fileName = Path.GetFileName(f), name = fileName, nodeCount = 0, connectionCount = 0 };
            }
        }).ToList();

        return Ok(samples);
    }

    /// <summary>Get a specific workflow sample as JSON.</summary>
    [HttpGet("samples/{name}")]
    public IActionResult GetSample(string name)
    {
        var samplesDir = GetSamplesDirectory();
        var filePath = Path.Combine(samplesDir, name.EndsWith(".json") ? name : $"{name}.json");

        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = "Sample not found." });

        var json = System.IO.File.ReadAllText(filePath);
        return Content(json, "application/json");
    }

    private string GetSamplesDirectory()
    {
        // 1. Check inside ContentRootPath (Docker: COPY'd into /app/workflow-samples)
        var samplesDir = Path.Combine(_env.ContentRootPath, "workflow-samples");
        if (Directory.Exists(samplesDir))
            return samplesDir;

        // 2. Fallback: one level up from ContentRootPath (local dev: solution root)
        samplesDir = Path.Combine(Directory.GetParent(_env.ContentRootPath)?.FullName ?? _env.ContentRootPath, "workflow-samples");
        return samplesDir;
    }

    private static string GetParam(Dictionary<string, object> p, string key)
        => p.TryGetValue(key, out var v) && v != null ? v.ToString() ?? "" : "";
}

public class AiGenerateRequest
{
    public string Prompt { get; set; } = "";
    public string? Name { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Temperature { get; set; }
}
