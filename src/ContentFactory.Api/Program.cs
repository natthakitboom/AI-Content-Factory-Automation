using System.Security.Claims;
using ContentFactory.Api.Endpoints;
using ContentFactory.Api.Middleware;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Infrastructure.Billing;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Security;
using ContentFactory.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

LocalBindingGuard.Validate(builder.Configuration["ASPNETCORE_URLS"]);

var connectionString = builder.Configuration.GetConnectionString("App");
if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = DatabaseConnection.FromEnvironment();
}

var storageOptions = StorageOptions.Create(builder.Configuration["DATA_ROOT"]);
var maxUploadBytes = StorageOptions.MaxVideoBytes + (1024 * 1024);

builder.Services.ConfigureHttpJsonOptions(options => JsonDefaults.Configure(options.SerializerOptions));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(BudgetOptions.FromEnvironment());
builder.Services.AddSingleton<LiveApprovalGuard>();
builder.Services.AddScoped<PriceSnapshotService>();
builder.Services.AddScoped<IBudgetGuard, PostgresBudgetGuard>();
builder.Services.AddSingleton(storageOptions);
builder.Services.AddScoped<FileSystemAssetStore>();
builder.Services.AddScoped<IAssetStore>(services => services.GetRequiredService<FileSystemAssetStore>());
builder.Services.AddScoped<AssetIngestService>();
builder.Services.AddScoped<ContentFactory.Infrastructure.Jobs.RunEnqueuer>();
builder.Services.AddScoped<ContentFactory.Infrastructure.Jobs.PostgresJobStore>(services =>
    new ContentFactory.Infrastructure.Jobs.PostgresJobStore(
        services.GetRequiredService<AppDbContext>(),
        services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ContentFactory.Infrastructure.Review.ReviewService>();
builder.Services.AddScoped<IReviewService>(services =>
    services.GetRequiredService<ContentFactory.Infrastructure.Review.ReviewService>());
builder.Services.AddScoped<ContentFactory.Infrastructure.Export.ExportService>();
builder.Services.AddScoped<IExportService>(services =>
    services.GetRequiredService<ContentFactory.Infrastructure.Export.ExportService>());
builder.Services.AddScoped<ContentFactory.Infrastructure.Export.PublicationRecorder>();
builder.Services.AddScoped<ContentFactory.Infrastructure.Analytics.MetricsImporter>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<IPasswordHasher<OwnerUser>, PasswordHasher<OwnerUser>>();
builder.Services.AddSingleton(new SecretRedactor(
[
    builder.Configuration["FAL_KEY"],
    builder.Configuration["AZURE_SPEECH_KEY"],
    builder.Configuration["LM_STUDIO_API_TOKEN"],
    builder.Configuration["DATABASE_PASSWORD"],
    builder.Configuration.GetConnectionString("App")
]));
builder.Services.AddProblemDetails();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "cf_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;

        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var principal = context.Principal;
            var idValue = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var versionValue = principal?.FindFirstValue(AuthEndpoints.SessionVersionClaim);

            if (!Guid.TryParse(idValue, out var ownerId)
                || !int.TryParse(versionValue, out var sessionVersion))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var owner = await db.OwnerUsers
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == ownerId, context.HttpContext.RequestAborted);

            if (owner is null || !owner.Active || owner.SessionVersion != sessionVersion)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseMiddleware<LocalRequestGuard>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<ExceptionMappingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfMiddleware>();
app.UseMiddleware<QuiesceMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
{
    var ready = await db.Database.CanConnectAsync(ct);
    return ready
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapProductEndpoints();
app.MapCampaignEndpoints();
app.MapAssetEndpoints();
app.MapReviewEndpoints();
app.MapExportEndpoints();
app.MapScheduleEndpoints();
app.MapAnalyticsEndpoints();
app.MapSystemEndpoints();

app.Run();

public partial class Program;
