using System.Text.Json;
using FileInviteLite.Api.Common.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// The handler is tested directly because TestServer cannot produce Kestrel's transport-level
/// failures: a body over the size limit (413) or a body timeout (408) is covered at this level
/// only, not through the HTTP pipeline.
/// </summary>
public sealed class UnreadableBodyHandlerTests
{
    private static async Task<(bool Handled, int Status, JsonElement Body)> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        context.Request.Method = "POST";
        context.Request.Path = "/api/requests";

        var handled = await new UnreadableBodyExceptionHandler(
            NullLogger<UnreadableBodyExceptionHandler>.Instance).TryHandleAsync(
                context, exception, CancellationToken.None);

        context.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
        return (handled, context.Response.StatusCode, body);
    }

    [Fact]
    public async Task Status400_IsAValidationProblemOnBody_WithoutTheFrameworkMessage_S10_1()
    {
        var (handled, status, body) = await HandleAsync(
            new BadHttpRequestException("Failed to bind parameter \"X\" from \"secret-value\"", 400));

        Assert.True(handled);
        Assert.Equal(400, status);
        Assert.Equal("https://fileinvite-lite/errors/validation", body.GetProperty("type").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("body", out _));
        Assert.DoesNotContain("secret-value", body.GetRawText());
    }

    [Fact]
    public async Task Status413_StaysPayloadTooLarge_NotFlattenedToValidation()
    {
        var (_, status, body) = await HandleAsync(new BadHttpRequestException("too big", 413));

        Assert.Equal(413, status);
        Assert.Equal(413, body.GetProperty("status").GetInt32());
        Assert.Equal("https://fileinvite-lite/errors/payload-too-large", body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Status415_StaysUnsupportedMediaType()
    {
        var (_, status, body) = await HandleAsync(new BadHttpRequestException("media", 415));

        Assert.Equal(415, status);
        Assert.Equal("https://fileinvite-lite/errors/unsupported-media-type", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Status408_KeepsItsStatusWithAGenericProblem()
    {
        var (_, status, body) = await HandleAsync(new BadHttpRequestException("timed out reading \"x\"", 408));

        Assert.Equal(408, status);
        Assert.Equal(408, body.GetProperty("status").GetInt32());
        Assert.False(body.TryGetProperty("errors", out _));
        Assert.DoesNotContain("timed out", body.GetRawText());
    }

    [Fact]
    public async Task OtherExceptions_AreLeftToTheGenericHandler()
    {
        var handled = await new UnreadableBodyExceptionHandler(
            NullLogger<UnreadableBodyExceptionHandler>.Instance).TryHandleAsync(
                new DefaultHttpContext(), new InvalidOperationException("boom"), CancellationToken.None);

        Assert.False(handled);
    }
}
