using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
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
builder.Services.AddSingleton(BudgetOptions.FromEnvironment());
builder.Services.AddSingleton<LiveApprovalGuard>();
builder.Services.AddScoped<PriceSnapshotService>();
builder.Services.AddScoped<IBudgetGuard, PostgresBudgetGuard>();
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
