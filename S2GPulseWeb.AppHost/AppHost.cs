using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var databaseProvider = builder.Configuration["DatabaseProvider"] ?? "PostgreSQL";
IResourceBuilder<IResourceWithConnectionString>? pulseweb = null;

if (!databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    // Use external PostgreSQL connection string from configuration
    // instead of spinning up a Docker container
    var externalConnectionString = builder.Configuration.GetConnectionString("pulsewebdb");
    
    if (!string.IsNullOrEmpty(externalConnectionString))
    {
        // External database - use connection string from config
        pulseweb = builder.AddConnectionString("pulsewebdb");
    }
    else
    {
        // Local development - use Docker container
        var postgres = builder.AddPostgres("postgres")
            .WithDataVolume("postgres-data")
            .WithLifetime(ContainerLifetime.Persistent);

        pulseweb = postgres.AddDatabase("pulsewebdb");
    }
}

var apiService = builder.AddProject<Projects.S2GPulseWeb_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

if (pulseweb != null)
{
    apiService.WithReference(pulseweb).WaitFor(pulseweb);
}

var webBuilder = builder.AddProject<Projects.S2GPulseWeb_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

if (pulseweb != null)
{
    webBuilder.WithReference(pulseweb);
}

builder.Build().Run();
