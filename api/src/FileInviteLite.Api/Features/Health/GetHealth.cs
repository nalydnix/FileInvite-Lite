using FileInviteLite.Api.Common;

namespace FileInviteLite.Api.Features.Health;

public sealed class GetHealth : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/health", Handle)
           .AllowAnonymous()
           .WithName(nameof(GetHealth));

    public sealed record Response(string Status);

    // Deliberately bare: no version, no environment name, no dependency detail. A health endpoint
    // is unauthenticated, so everything it returns is public (S10.5).
    private static IResult Handle() => Results.Ok(new Response("healthy"));
}
