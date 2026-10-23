using System.Reflection;

namespace FileInviteLite.Api.Common;

/// <summary>
/// Finds every <see cref="IEndpoint"/> in this assembly and maps it.
/// </summary>
public static class EndpointRegistration
{
    /// <summary>
    /// Every concrete <see cref="IEndpoint"/> in the API assembly. Exposed so a test can assert
    /// that each one was actually mapped — a mistyped class would otherwise vanish silently,
    /// which is the one real risk of registering by reflection.
    /// </summary>
    public static IReadOnlyList<Type> DiscoverEndpointTypes() =>
        [.. Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IEndpoint).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal)];

    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var type in DiscoverEndpointTypes())
        {
            var endpoint = (IEndpoint)Activator.CreateInstance(type)!;
            endpoint.Map(app);
        }

        return app;
    }
}
