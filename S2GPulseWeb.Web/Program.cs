using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using S2GPulseWeb.Web;
using S2GPulseWeb.Web.Components;
using S2GPulseWeb.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Configure forwarded headers for reverse proxy (Azure Container Apps)
// This ensures OAuth redirect URIs use HTTPS when behind a load balancer
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Clear known networks/proxies to trust all proxies (required for cloud environments)
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add DbContext Factory with conditional provider selection
var databaseProvider = builder.Configuration["DatabaseProvider"] ?? "PostgreSQL";
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
{
    if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var connectionString = builder.Configuration.GetConnectionString("SqliteConnection") 
            ?? "Data Source=pulseweb.db";
        options.UseSqlite(connectionString);
    }
    else
    {
        var connectionString = builder.Configuration.GetConnectionString("pulsewebdb")
            ?? builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=pulsewebdb;Username=postgres;Password=postgres";
        options.UseNpgsql(connectionString);
    }
});

// Add Identity services
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// Configure cookie authentication
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(24);
    options.SlidingExpiration = true;
});

// Add external authentication providers (Microsoft and Google SSO)
var microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
var microsoftClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];

var authBuilder = builder.Services.AddAuthentication();

// API Key authentication scheme
authBuilder.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
    S2GPulseWeb.Web.Logic.ApiKeyAuthenticationHandler>("ApiKey", null);

if (!string.IsNullOrEmpty(microsoftClientId) && !string.IsNullOrEmpty(microsoftClientSecret))
{
    authBuilder.AddMicrosoftAccount(options =>
    {
        options.ClientId = microsoftClientId;
        options.ClientSecret = microsoftClientSecret;
    });
}

if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
    });
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add API controllers (for ListenerProxyController)
builder.Services.AddControllers();

builder.Services.AddMemoryCache();
builder.Services.AddOutputCache();

// Radzen Blazor services (required for HtmlEditor)
builder.Services.AddScoped<Radzen.DialogService>();
builder.Services.AddScoped<Radzen.NotificationService>();
builder.Services.AddScoped<Radzen.TooltipService>();
builder.Services.AddScoped<Radzen.ContextMenuService>();

builder.Services.AddScoped<S2GPulseWeb.Web.Logic.NodeExecutorFactory>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.UserSecretService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.AnthropicModelService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.GeminiModelService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.MistralModelService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.GroqModelService>();
builder.Services.AddSingleton<S2GPulseWeb.Web.Logic.NodeExecutionManager>();
builder.Services.AddSingleton<S2GPulseWeb.Web.Logic.OpenClawWsSessionManager>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.WorkflowService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.NodeLogService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.UserPreferenceService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.OAuthService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.PlatformConnectorService>();
builder.Services.AddSingleton<S2GPulseWeb.Web.Logic.WorkflowExecutionService>();
builder.Services.AddSingleton<S2GPulseWeb.Web.Logic.CacheStorageService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.StorageTableService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.VectorDbService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.CustomNodeService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.KnowledgeBaseService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.CopilotConnectorService>();
builder.Services.AddSingleton<S2GPulseWeb.Web.Logic.BuiltInNodeCatalogService>();
builder.Services.AddSingleton<S2GPulseWeb.Web.Logic.NodeKnowledgeService>();
builder.Services.AddHostedService<S2GPulseWeb.Web.Logic.LogCleanupBackgroundService>();
builder.Services.AddHostedService<S2GPulseWeb.Web.Logic.WorkflowAutoStartService>();
builder.Services.AddHostedService<S2GPulseWeb.Web.Logic.OrganizationCleanupBackgroundService>();

// Workflow API services
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.ApiKeyService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.WorkflowApiService>();

// Subscription and billing services
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.SubscriptionService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.MembershipPlanService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.UsageTrackingService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.StripeService>();

// Legal and content management services
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.LegalDocumentService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.DeveloperNoteService>();

// Organization services
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.OrganizationService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.OrganizationContextService>();
builder.Services.AddScoped<S2GPulseWeb.Web.Logic.OrganizationUsageTrackingService>();

// Platform branding (white-label)
builder.Services.AddSingleton<S2GPulseWeb.Web.Logic.PlatformSettingsService>();

builder.Services.AddHttpClient<WeatherApiClient>(client => client.BaseAddress = new("http://apiservice"));
builder.Services.AddHttpClient<S2GPulseWeb.Web.Logic.WorkflowAssistantService>(client =>
{
    // OpenAI requests can take a long time - set a generous timeout (5 minutes)
    client.Timeout = TimeSpan.FromMinutes(10);
}).ConfigureHttpClient(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Disable automatic decompression timeout issues
}).RemoveAllLoggers(); // Remove standard resilience logging that might timeout

// Configure default HttpClient with extended timeout for workflow operations
builder.Services.AddHttpClient("", client =>
{
    // Default timeout for all HttpClients - extended for long-running workflows
    client.Timeout = TimeSpan.FromMinutes(10);
});

var app = builder.Build();

// Apply migrations automatically with retry logic
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

    logger.LogInformation("Attempting to initialize database (Provider: {Provider})...", databaseProvider);

    if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        // For SQLite in development, we can simplify by ensuring created
        // Or handle SQLite specific migrations if they existed.
        // Given existing migrations are Npgsql-based, EnsureCreated is safer for fresh SQLite.
        try 
        {
            await dbContext.Database.EnsureCreatedAsync();
            logger.LogInformation("SQLite database initialized successfully via EnsureCreated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize SQLite database.");
        }
    }
    else
    {
        var retryCount = 0;
        var maxRetries = 5;
        var retryDelay = TimeSpan.FromSeconds(5);
        
        while (retryCount < maxRetries)
        {
            try
            {
                // Check if database can be connected
                var canConnect = await dbContext.Database.CanConnectAsync();
                if (!canConnect)
                {
                    logger.LogWarning("Cannot connect to database. Attempt {RetryCount} of {MaxRetries}", retryCount + 1, maxRetries);
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        await Task.Delay(retryDelay);
                        continue;
                    }
                    throw new Exception("Failed to connect to database after multiple attempts");
                }
                
                // Apply pending migrations
                var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
                if (pendingMigrations.Any())
                {
                    logger.LogInformation("Applying {Count} pending migrations: {Migrations}", 
                        pendingMigrations.Count(), 
                        string.Join(", ", pendingMigrations));
                    await dbContext.Database.MigrateAsync();
                    logger.LogInformation("Database migrations applied successfully");
                }
                else
                {
                    logger.LogInformation("Database is up to date. No migrations to apply.");
                }
                
                break; // Success - exit retry loop
            }
            catch (Exception ex)
            {
                retryCount++;
                if (retryCount >= maxRetries)
                {
                    logger.LogError(ex, "Failed to apply database migrations after {MaxRetries} attempts. The application will start but database operations may fail.", maxRetries);
                    break;
                }
                
                logger.LogWarning(ex, "Error applying migrations (attempt {RetryCount} of {MaxRetries}). Retrying in {Delay} seconds...", 
                    retryCount, maxRetries, retryDelay.TotalSeconds);
                await Task.Delay(retryDelay);
            }
        }
    }
}

// Seed initial legal documents if needed
using (var scope = app.Services.CreateScope())
{
    var legalDocumentService = scope.ServiceProvider.GetRequiredService<S2GPulseWeb.Web.Logic.LegalDocumentService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        await legalDocumentService.SeedInitialDocumentsAsync();
        logger.LogInformation("Legal documents seeding completed.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed legal documents. This is non-critical.");
    }
}

// Seed membership plans if needed
using (var scope = app.Services.CreateScope())
{
    var membershipPlanService = scope.ServiceProvider.GetRequiredService<S2GPulseWeb.Web.Logic.MembershipPlanService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        await membershipPlanService.SeedDefaultPlansAsync();
        logger.LogInformation("Membership plans seeding completed.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed membership plans. This is non-critical.");
    }
}

// Seed node categories and custom node definitions if needed
using (var scope = app.Services.CreateScope())
{
    var customNodeService = scope.ServiceProvider.GetRequiredService<S2GPulseWeb.Web.Logic.CustomNodeService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        await customNodeService.SeedCategoriesAndNodesAsync();
        logger.LogInformation("Node categories and custom nodes seeding completed.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed node categories/custom nodes. This is non-critical.");
    }
}

// Enable forwarded headers (must be first middleware for reverse proxy support)
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.UseOutputCache();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Logout endpoint
app.MapPost("/account/perform-logout", async (SignInManager<S2GPulseWeb.Web.Data.ApplicationUser> signInManager, HttpContext httpContext) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/account/login");
}).RequireAuthorization();

// External login challenge endpoint (for SSO buttons)
app.MapPost("/account/external-login", async (HttpContext context, SignInManager<S2GPulseWeb.Web.Data.ApplicationUser> signInManager) =>
{
    var form = await context.Request.ReadFormAsync();
    var provider = form["provider"].ToString();
    var returnUrl = form["returnUrl"].ToString() ?? "/workflow";

    if (string.IsNullOrEmpty(provider))
    {
        return Results.Redirect("/account/login?error=provider-missing");
    }

    var redirectUrl = $"/account/external-login-callback?returnUrl={Uri.EscapeDataString(returnUrl)}";
    var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
    
    return Results.Challenge(properties, [provider]);
});

// WebSocket support for OpenClaw bridge (must be before MapControllers)
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseMiddleware<S2GPulseWeb.Web.Middleware.OpenClawWsMiddleware>();

// Map API controllers (ListenerProxyController for Azure Function proxy)
app.MapControllers();

app.MapDefaultEndpoints();

// Health check endpoint for Docker HEALTHCHECK
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
