using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication() // Use ASP.NET Core integration for proper HTTP body handling
    .ConfigureServices(services =>
    {
        // Configure HttpClient with 4m30s timeout (Azure Functions max runtime is 5 minutes)
        services.AddHttpClient("", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(270); // 4 minutes 30 seconds
        });
    })
    .Build();

host.Run();
