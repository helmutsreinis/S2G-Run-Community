using Microsoft.AspNetCore.Mvc;

namespace S2GPulseWeb.Web.Controllers;

/// <summary>
/// Serves remote client scripts (Python/PowerShell) with pre-configured variables.
/// No authentication required — the scripts contain no secrets, only node routing IDs.
/// </summary>
[ApiController]
[Route("api/remote-client")]
public class RemoteClientController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private const string ProxyUrl = "https://listener.s2g.run/api";

    public RemoteClientController(IWebHostEnvironment env)
    {
        _env = env;
    }

    /// <summary>
    /// Download the Python remote client pre-configured with the provided listener and client IDs.
    /// </summary>
    [HttpGet("python")]
    public IActionResult GetPythonClient(
        [FromQuery] string listenerId,
        [FromQuery] string clientId)
    {
        if (string.IsNullOrWhiteSpace(listenerId) || string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { error = "listenerId and clientId query parameters are required." });

        var scriptPath = Path.Combine(_env.ContentRootPath, "..", "clients", "remote_client.py");
        if (!System.IO.File.Exists(scriptPath))
            return NotFound(new { error = "Python client script not found on server." });

        var content = System.IO.File.ReadAllText(scriptPath);
        content = ReplacePlaceholders(content, listenerId, clientId);

        return File(
            System.Text.Encoding.UTF8.GetBytes(content),
            "text/x-python",
            "remote_client.py");
    }

    /// <summary>
    /// Download the PowerShell remote client pre-configured with the provided listener and client IDs.
    /// </summary>
    [HttpGet("powershell")]
    public IActionResult GetPowerShellClient(
        [FromQuery] string listenerId,
        [FromQuery] string clientId)
    {
        if (string.IsNullOrWhiteSpace(listenerId) || string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { error = "listenerId and clientId query parameters are required." });

        var scriptPath = Path.Combine(_env.ContentRootPath, "..", "clients", "RemoteClient.ps1");
        if (!System.IO.File.Exists(scriptPath))
            return NotFound(new { error = "PowerShell client script not found on server." });

        var content = System.IO.File.ReadAllText(scriptPath);
        content = ReplacePlaceholders(content, listenerId, clientId);

        // PowerShell 5.x requires UTF-8 BOM to correctly parse multi-byte chars (emojis)
        var utf8Bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = utf8Bom.GetPreamble();
        var contentBytes = utf8Bom.GetBytes(content);
        var result = new byte[preamble.Length + contentBytes.Length];
        preamble.CopyTo(result, 0);
        contentBytes.CopyTo(result, preamble.Length);

        return File(
            result,
            "application/octet-stream",
            "RemoteClient.ps1");
    }

    private static string ReplacePlaceholders(string content, string listenerId, string clientId)
    {
        return content
            .Replace("__PLACEHOLDER_PROXY_URL__", ProxyUrl)
            .Replace("__PLACEHOLDER_LISTENER_ID__", listenerId)
            .Replace("__PLACEHOLDER_CLIENT_ID__", clientId);
    }
}

