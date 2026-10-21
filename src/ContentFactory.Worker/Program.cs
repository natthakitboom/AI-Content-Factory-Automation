using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Providers.Fal;
using ContentFactory.Infrastructure.Security;
using ContentFactory.Infrastructure.Storage;
using ContentFactory.Worker;
using ContentFactory.Worker.Handlers;
using ContentFactory.Worker.Health;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("App");
if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = DatabaseConnection.FromEnvironment();
}

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(StorageOptions.Create(builder.Configuration["DATA_ROOT"]));
builder.Services.AddSingleton(BudgetOptions.FromEnvironment());
builder.Services.AddSingleton<LiveApprovalGuard>();
builder.Services.AddScoped<PriceSnapshotService>();
builder.Services.AddScoped<IBudgetGuard, PostgresBudgetGuard>();
builder.Services.AddScoped<FileSystemAssetStore>();
builder.Services.AddScoped<IAssetStore>(services =>
    services.GetRequiredService<FileSystemAssetStore>());
builder.Services.AddSingleton<IProcessRunner, SafeProcessRunner>();
builder.Services.AddScoped<ITechnicalQa, TechnicalQa>();
builder.Services.AddScoped<TechnicalQaHandler>();
builder.Services.AddScoped<IJobHandler>(services =>
    services.GetRequiredService<TechnicalQaHandler>());
builder.Services.AddSingleton(FalOptions.FromEnvironment());
builder.Services.AddHttpClient<IVideoProvider, FalVideoProvider>();
builder.Services.AddScoped<SubmitVideoHandler>();
builder.Services.AddScoped<IJobHandler>(services =>
    services.GetRequiredService<SubmitVideoHandler>());
builder.Services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
builder.Services.AddSingleton(new SafeMediaDownloaderOptions());
builder.Services.AddHttpClient<SafeMediaDownloader>()
    .ConfigurePrimaryHttpMessageHandler(services =>
        SafeMediaDownloader.CreateHandler(services.GetRequiredService<IHostAddressResolver>()));
builder.Services.AddScoped<PollVideoHandler>();
builder.Services.AddScoped<IJobHandler>(services =>
    services.GetRequiredService<PollVideoHandler>());
builder.Services.AddScoped<DownloadVideoHandler>();
builder.Services.AddScoped<IJobHandler>(services =>
    services.GetRequiredService<DownloadVideoHandler>());
builder.Services.AddScoped<ProviderReconciler>();
builder.Services.AddSingleton<ResourceGovernor>();
builder.Services.AddScoped<PostgresJobStore>(services =>
    new PostgresJobStore(
        services.GetRequiredService<AppDbContext>(),
        services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IJobStore>(services =>
    services.GetRequiredService<PostgresJobStore>());
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddScoped<LeaseWatchdog>();
builder.Services.AddScoped<WorkflowOutboxHandler>();
builder.Services.AddScoped<JobDispatcher>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
