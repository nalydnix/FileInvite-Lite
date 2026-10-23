using FileInviteLite.Api.Common;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FileInviteLite.Api.Tests.Infrastructure;

/// <summary>
/// Registering endpoints by reflection has exactly one real risk: a class that should have been
/// mapped silently is not, and nobody notices until a route 404s in production. These tests close
/// that gap.
/// </summary>
public sealed class EndpointRegistrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void EveryIEndpointImplementationIsMapped()
    {
        var discovered = EndpointRegistration.DiscoverEndpointTypes();
        Assert.NotEmpty(discovered);

        var mappedNames = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .Select(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        // Each endpoint maps itself with .WithName(nameof(TheClass)), so the class name is the
        // link between "exists in the assembly" and "is reachable over HTTP".
        var missing = discovered
            .Select(t => t.Name)
            .Where(name => !mappedNames.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These IEndpoint types were never mapped: {string.Join(", ", missing)}. " +
            "Check the class has .WithName(nameof(TheClass)) and a parameterless constructor.");
    }

    [Fact]
    public void EveryEndpointTypeHasAParameterlessConstructor()
    {
        // Activator.CreateInstance is how MapEndpoints builds them; a constructor parameter would
        // throw at startup rather than at build time. Dependencies belong on the handler method,
        // where the framework injects them.
        foreach (var type in EndpointRegistration.DiscoverEndpointTypes())
        {
            Assert.True(
                type.GetConstructor(Type.EmptyTypes) is not null,
                $"{type.Name} needs a parameterless constructor; inject dependencies into Handle instead.");
        }
    }
}
