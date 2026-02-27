using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace S2GPulseWeb.Web.Logic;

/// <summary>
/// Custom authentication handler for X-API-Key header-based API access.
/// Creates a ClaimsPrincipal with the API key owner's identity.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly ApiKeyService _apiKeyService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyValues))
        {
            return AuthenticateResult.NoResult();
        }

        var rawKey = apiKeyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return AuthenticateResult.Fail("API key is empty.");
        }

        var userId = await _apiKeyService.ValidateKeyAsync(rawKey);
        if (userId == null)
        {
            return AuthenticateResult.Fail("Invalid, expired, or revoked API key.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("auth_method", "api_key")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.ContentType = "application/json";
        return Response.WriteAsync("{\"error\":\"Missing or invalid X-API-Key header.\"}");
    }
}
