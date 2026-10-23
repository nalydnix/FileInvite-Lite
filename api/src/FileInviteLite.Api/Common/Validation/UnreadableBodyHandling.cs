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
        if (exception is not BadHttpRequestException)
        {
            return false;
        }

        // The framework's message describes the parser's internals and can quote the payload, so
        // it goes to the log and never into the body.
        logger.LogWarning(exception, "A request body could not be read.");

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ProblemResults
            .Validation("body", "The request body could not be read.")
            .ExecuteAsync(context);

        return true;
    }
}
