using FileInviteLite.Api.Common.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace FileInviteLite.Api.Common.Validation;

/// <summary>
/// Makes a request the framework cannot bind — malformed JSON, a date that will not parse — leave
/// in the shared problem shape in every environment (S10.1).
/// </summary>
/// <remarks>
/// ASP.NET reports a bad request differently by environment: in Development it throws
/// <see cref="BadHttpRequestException"/>, in Production it writes its own 400 with a different
/// <c>type</c> and no <c>errors</c>. Throwing everywhere puts both through one handler, so
/// Development cannot pass while Production quietly differs.
/// </remarks>
public static class UnreadableBodyHandling
{
    public static IServiceCollection AddUnreadableBodyHandling(this IServiceCollection services)
    {
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddExceptionHandler<UnreadableBodyExceptionHandler>();
        return services;
    }
}

public sealed class UnreadableBodyExceptionHandler(ILogger<UnreadableBodyExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException bad)
        {
            return false;
        }

        // The framework's message can quote client input — a route or query binding failure
        // includes the submitted value in full — so the default log carries only where it
        // happened, and the exception itself is there for a developer who turns Debug on.
        logger.LogWarning(
            "A request could not be read: {ExceptionType} on {Method} {Path}.",
            exception.GetType().Name, context.Request.Method, context.Request.Path);
        logger.LogDebug(exception, "Details of the unreadable request.");

        // Keep the status the framework chose: only a plain 400 is a validation failure. A body
        // over the size limit (413) or one that took too long (408) must not be flattened to it.
        IResult problem = bad.StatusCode switch
        {
            StatusCodes.Status400BadRequest =>
                ProblemResults.Validation("body", "The request body could not be read."),
            StatusCodes.Status413PayloadTooLarge =>
                ProblemResults.PayloadTooLarge("The request body is too large."),
            StatusCodes.Status415UnsupportedMediaType =>
                ProblemResults.UnsupportedMediaType("The request content type is not supported."),
            _ => TypedResults.Problem(
                detail: "The request could not be processed.", statusCode: bad.StatusCode),
        };

        context.Response.StatusCode = bad.StatusCode;
        await problem.ExecuteAsync(context);

        return true;
    }
}
