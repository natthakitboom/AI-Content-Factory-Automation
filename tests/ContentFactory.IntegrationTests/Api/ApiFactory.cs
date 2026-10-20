using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContentFactory.IntegrationTests.Api;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _dataRoot;
    private readonly CapturingLoggerProvider _logs;

    public ApiFactory(string connectionString, string dataRoot, CapturingLoggerProvider logs)
    {
        _connectionString = connectionString;
        _dataRoot = dataRoot;
        _logs = logs;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:App", _connectionString);
        builder.UseSetting("DATA_ROOT", _dataRoot);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);
        });
    }
}
