using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Providers;
using ContentFactory.Infrastructure.Providers.Fal;
using ContentFactory.Infrastructure.Providers.LmStudio;
using ContentFactory.Infrastructure.Providers.Mock;
using ContentFactory.Infrastructure.Providers.Voice;
using ContentFactory.Core.Campaigns;
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
var modes = ProviderModes.FromEnvironment();
builder.Services.AddSingleton(modes);
builder.Services.AddSingleton(FalOptions.FromEnvironment());
builder.Services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
builder.Services.AddSingleton(new SafeMediaDownloaderOptions());

if (modes.Video == "fal")
{
    builder.Services.AddHttpClient<IVideoProvider, FalVideoProvider>();
    builder.Services.AddHttpClient<SafeMediaDownloader>()
        .ConfigurePrimaryHttpMessageHandler(services =>
            SafeMediaDownloader.CreateHandler(services.GetRequiredService<IHostAddressResolver>()));
}
else
{
    // Mock mode has no route to the network: the only "host" is an in-process clip.
    builder.Services.AddSingleton(services => new MockMediaHandler(token =>
        MockClipGenerator.GenerateAsync(services.GetRequiredService<IProcessRunner>(), token)));
    builder.Services.AddSingleton<IVideoProvider, MockVideoProvider>();
    builder.Services.AddScoped(services => new SafeMediaDownloader(
        new HttpClient(services.GetRequiredService<MockMediaHandler>(), disposeHandler: false),
        services.GetRequiredService<SafeMediaDownloaderOptions>(),
        new MockHostResolver()));
}

builder.Services.AddSingleton<IScriptValidator, ScriptValidator>();
if (modes.Text == "lm_studio")
{
    builder.Services.AddSingleton(LmStudioOptions.FromEnvironment());
    builder.Services.AddHttpClient<IScriptPlanner, LmStudioScriptPlanner>();
}
else
{
    builder.Services.AddSingleton<IScriptPlanner, MockScriptPlanner>();
}

builder.Services.AddScoped<IVoiceProvider>(services => modes.Voice == "mock"
    ? new MockVoiceProvider(services.GetRequiredService<IAssetStore>())
    : new UnconfiguredVoiceProvider(modes.Voice));
builder.Services.AddScoped(services => new VoiceAssembler(
    services.GetRequiredService<IAssetStore>(),
    new FfprobeAudioProbe(
        services.GetRequiredService<IAssetStore>(),
        services.GetRequiredService<StorageOptions>()),
    new FfmpegAudioConcatenator(
        services.GetRequiredService<IAssetStore>(),
        services.GetRequiredService<StorageOptions>()),
    new FileVoiceAssetCache(services.GetRequiredService<StorageOptions>())));
builder.Services.AddScoped(services => new ConfiguredMedia(
    services.GetRequiredService<IAssetStore>(),
    services.GetRequiredService<StorageOptions>(),
    services.GetRequiredService<IProcessRunner>(),
    () => Environment.GetEnvironmentVariable("CAPTION_FONT_FILE")));
builder.Services.AddScoped<ITimelineCompiler, ConfiguredTimelineCompiler>();
builder.Services.AddScoped<IVideoRenderer, ConfiguredVideoRenderer>();
builder.Services.AddSingleton(new VoiceHandlerOptions(
    modes.Voice,
    Environment.GetEnvironmentVariable("AZURE_SPEECH_VOICE_ID") ?? modes.Voice,
    modes.Voice + "-v1",
    modes.Voice + "-config-v1"));

builder.Services.AddScoped<SubmitVideoHandler>(services => new SubmitVideoHandler(
    services.GetRequiredService<AppDbContext>(),
    services.GetRequiredService<IVideoProvider>(),
    services.GetRequiredService<IBudgetGuard>(),
    services.GetRequiredService<PriceSnapshotService>(),
    services.GetRequiredService<FalOptions>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<BudgetOptions>()));
builder.Services.AddScoped<PollVideoHandler>();
builder.Services.AddScoped<DownloadVideoHandler>();
builder.Services.AddScoped<ProviderReconciler>();
builder.Services.AddScoped<PlanScriptHandler>();
builder.Services.AddScoped<PrepareVisualsHandler>();
builder.Services.AddScoped<SynthesizeVoiceHandler>();
builder.Services.AddScoped<CompileTimelineHandler>();
builder.Services.AddScoped<RenderVideoHandler>();
builder.Services.AddScoped<PrepareReviewHandler>();
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<SubmitVideoHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<PollVideoHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<DownloadVideoHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<PlanScriptHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<PrepareVisualsHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<SynthesizeVoiceHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<CompileTimelineHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<RenderVideoHandler>());
builder.Services.AddScoped<IJobHandler>(s => s.GetRequiredService<PrepareReviewHandler>());
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
