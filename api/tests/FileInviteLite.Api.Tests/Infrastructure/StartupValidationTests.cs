using Microsoft.Extensions.Options;

namespace FileInviteLite.Api.Tests.Infrastructure;

public sealed class StartupValidationTests
{
    private sealed class NoSigningKeyFactory : ApiFactory
    {
        protected override IDictionary<string, string?> Configuration =>
            new Dictionary<string, string?> { ["Jwt:SigningKey"] = "" };
    }

    private sealed class ShortSigningKeyFactory : ApiFactory
    {
        protected override IDictionary<string, string?> Configuration =>
            new Dictionary<string, string?> { ["Jwt:SigningKey"] = "too-short" };
    }

    [Fact]
    public void Startup_WithNoSigningKey_FailsFastWithAUsefulMessage_S8_7()
    {
        using var factory = new NoSigningKeyFactory();

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        // The message has to tell a developer what to do, because this is the first thing that
        // happens on a fresh clone.
        Assert.Contains("Jwt:SigningKey", error.Message, StringComparison.Ordinal);
        Assert.Contains("user-secrets", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_WithAShortSigningKey_FailsFast_S8_7()
    {
        using var factory = new ShortSigningKeyFactory();

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("at least 32", error.Message, StringComparison.Ordinal);
    }
}
