using ContentFactory.Core.Exceptions;
using Microsoft.AspNetCore.Http;

namespace ContentFactory.Api.Middleware;

/// <summary>
/// Converts typed domain failures into ProblemDetails with stable codes. Unexpected
/// exceptions become a generic 500: stack traces, file paths, request bodies and secrets
/// never reach the client.
/// </summary>
public sealed class ExceptionMappingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMappingMiddleware> _logger;

    public ExceptionMappingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionMappingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException exception) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                exception.Code,
                exception.Message)
                .ExecuteAsync(context);
        }
        catch (NotFoundException exception) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status404NotFound,
                exception.Code,
                exception.Message)
                .ExecuteAsync(context);
        }
        catch (ConflictException exception) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message)
                .ExecuteAsync(context);
        }
        catch (PaidApisDisabledException exception) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status403Forbidden,
                exception.Code,
                exception.Message)
                .ExecuteAsync(context);
        }
        catch (ApprovalStaleException exception) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status403Forbidden,
                exception.Code,
                exception.Message)
                .ExecuteAsync(context);
        }
        catch (PriceStaleException exception) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message)
                .ExecuteAsync(context);
        }
        catch (BudgetExceededException exception) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message)
                .ExecuteAsync(context);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            var status = exception.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status400BadRequest;

            await ApiProblem.Create(
                status,
                status == StatusCodes.Status413PayloadTooLarge
                    ? "ASSET_TOO_LARGE"
                    : "REQUEST_INVALID",
                status == StatusCodes.Status413PayloadTooLarge
                    ? "The uploaded body is larger than the configured limit."
                    : "The request could not be read.")
                .ExecuteAsync(context);
        }
        catch (InvalidDataException) when (!context.Response.HasStarted)
        {
            await ApiProblem.Create(
                StatusCodes.Status413PayloadTooLarge,
                "ASSET_TOO_LARGE",
                "The uploaded body is larger than the configured limit.")
                .ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is no response to write.
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            _logger.LogError(exception, "Unhandled request failure for {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await ApiProblem.Create(
                StatusCodes.Status500InternalServerError,
                "INTERNAL_ERROR",
                "An unexpected error occurred.")
                .ExecuteAsync(context);
        }
    }
}
