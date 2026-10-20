using System.Security.Claims;
using ContentFactory.Api.Middleware;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Api.Endpoints;

public static class AuthEndpoints
{
    public const string SessionVersionClaim = "cf_sv";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth");

        group.MapGet("/csrf", (HttpContext http) =>
        {
            var token = CsrfMiddleware.CreateToken();
            http.Response.Cookies.Append(
                CsrfMiddleware.CookieName,
                token,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = http.Request.IsHttps,
                    Path = "/",
                    MaxAge = TimeSpan.FromHours(8)
                });
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new CsrfResponse(token));
        }).AllowAnonymous();

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        IPasswordHasher<OwnerUser> hasher,
        LoginThrottle throttle,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(request.Password))
        {
            return ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "INVALID_REQUEST",
                "Username and password are required.");
        }

        var now = clock.GetUtcNow();
        var throttleKey = $"{http.Connection.RemoteIpAddress?.ToString() ?? "loopback"}:{username}";
        if (throttle.IsBlocked(throttleKey, now))
        {
            http.Response.Headers.RetryAfter = "300";
            return ApiProblem.Create(
                StatusCodes.Status429TooManyRequests,
                "LOGIN_THROTTLED",
                "Too many failed login attempts. Try again later.");
        }

        var owner = await db.OwnerUsers
            .SingleOrDefaultAsync(x => x.Username == username && x.Active, ct);

        var verification = owner is null
            ? PasswordVerificationResult.Failed
            : hasher.VerifyHashedPassword(owner, owner.PasswordHash, request.Password);

        if (owner is null || verification == PasswordVerificationResult.Failed)
        {
            throttle.RecordFailure(throttleKey, now);
            return ApiProblem.Create(
                StatusCodes.Status401Unauthorized,
                "INVALID_CREDENTIALS",
                "Invalid username or password.");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            owner.PasswordHash = hasher.HashPassword(owner, request.Password);
            await db.SaveChangesAsync(ct);
        }

        throttle.Reset(throttleKey);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString()),
                new Claim(ClaimTypes.Name, owner.Username),
                new Claim(SessionVersionClaim, owner.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = now.AddHours(8)
            });

        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAsync(
        AppDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var idValue = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(idValue, out var ownerId))
        {
            var owner = await db.OwnerUsers.SingleOrDefaultAsync(x => x.Id == ownerId, ct);
            if (owner is not null)
            {
                owner.SessionVersion++;
                await db.SaveChangesAsync(ct);
            }
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record CsrfResponse(string Token);
}
