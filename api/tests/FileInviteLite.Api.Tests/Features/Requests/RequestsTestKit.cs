using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// Shared helpers for the request-builder tests: request bodies, direct seeding, and the one
/// assertion every 400 in this feature must pass (S10.1). Lives next to the tests, not in
/// Infrastructure/, because it is specific to this feature.
/// </summary>
internal static class RequestsTestKit
{
    public const string ValidationType = "https://fileinvite-lite/errors/validation";

    // ---- request bodies -------------------------------------------------------------------

    public static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);

    public static Dictionary<string, object?> Item(
        string title = "Passport", string? description = "Photo page", bool required = true) =>
        new() { ["title"] = title, ["description"] = description, ["required"] = required };

    public static Dictionary<string, object?> Body(
        string clientName = "Jane Client",
        string clientEmail = "jane@example.com",
        DateOnly? dueDate = null,
        IEnumerable<object?>? items = null) =>
        new()
        {
            ["clientName"] = clientName,
            ["clientEmail"] = clientEmail,
            ["dueDate"] = dueDate ?? UtcToday.AddDays(14),
            ["items"] = (items ?? [Item(), Item("Payslip", null, required: false)]).ToList(),
        };

    public static Task<HttpResponseMessage> PostRequestAsync(
        this HttpClient client, Dictionary<string, object?> body) =>
        client.PostAsJsonAsync("/api/requests", body);

    /// <summary>Creates a request through the API and returns its id.</summary>
    public static async Task<Guid> CreateAsync(this HttpClient client, Dictionary<string, object?>? body = null)
    {
        var response = await client.PostRequestAsync(body ?? Body());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    // ---- database access ------------------------------------------------------------------

    public static async Task<T> RunDbAsync<T>(this IServiceProvider services, Func<AppDbContext, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static Task<Guid> DemoStaffIdAsync(this IServiceProvider services) =>
        services.RunDbAsync(async db =>
            (await db.StaffUsers.AsNoTracking().SingleAsync(u => u.Email == ApiFactory.DemoEmail)).Id);

    public static Task<int> ResetRequestsAsync(this IServiceProvider services) =>
        services.RunDbAsync(async db =>
        {
            // Items first: no cascade deletes exist (ADR 0006). Audit events are left alone.
            await db.ChecklistItems.ExecuteDeleteAsync();
            return await db.DocumentRequests.ExecuteDeleteAsync();
        });

    /// <summary>
    /// Inserts a request straight through the DbContext, so a test can have rows the API itself
    /// refuses to create (a due date in the past, a status other than Draft).
    /// </summary>
    public static Task<Guid> SeedAsync(
        this IServiceProvider services,
        DateOnly dueDate,
        RequestStatus status = RequestStatus.Draft,
        int itemCount = 1,
        int approvedCount = 0,
        DateTime? createdAt = null,
        string clientName = "Seeded Client")
    {
        return services.RunDbAsync(async db =>
        {
            var staffId = await db.StaffUsers.Where(u => u.Email == ApiFactory.DemoEmail)
                .Select(u => u.Id).SingleAsync();

            var request = new DocumentRequest
            {
                ClientName = clientName,
                ClientEmail = "seeded@example.com",
                DueDate = dueDate,
                Status = status,
                CreatedBy = staffId,
                CreatedAt = createdAt ?? DateTime.UtcNow,
            };

            for (var i = 0; i < itemCount; i++)
            {
                request.Items.Add(new ChecklistItem
                {
                    RequestId = request.Id,
                    Title = $"Item {i + 1}",
                    Required = true,
                    Status = i < approvedCount ? ItemStatus.Approved : ItemStatus.Outstanding,
                });
            }

            db.DocumentRequests.Add(request);
            await db.SaveChangesAsync();
            return request.Id;
        });
    }

    // ---- response assertions --------------------------------------------------------------

    private static readonly Regex CamelCasePath =
        new(@"^[a-z][A-Za-z0-9]*(\[\d+\])?(\.[a-z][A-Za-z0-9]*(\[\d+\])?)*$", RegexOptions.Compiled);

    private static readonly string[] FluentValidationDefaultMarkers =
        ["propertyName", "errorMessage", "attemptedValue", "errorCode", "severity", "isValid"];

    /// <summary>
    /// The shape every 400 must have (S10.1, api-contract.md "Error format"): RFC 7807 content
    /// type, the project's validation type, a traceId, and an <c>errors</c> dictionary of
    /// camelCase field keys to message arrays. FluentValidation's own default format must not
    /// leak. Returns the <c>errors</c> object so a test can look further.
    /// </summary>
    public static async Task<JsonElement> AssertValidationProblemAsync(
        HttpResponseMessage response, params string[] expectedFields)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        Assert.Equal(ValidationType, root.GetProperty("type").GetString());
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));

        foreach (var marker in FluentValidationDefaultMarkers)
        {
            Assert.DoesNotContain(marker, raw, StringComparison.OrdinalIgnoreCase);
        }

        var errors = root.GetProperty("errors");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.NotEmpty(errors.EnumerateObject());

        foreach (var field in errors.EnumerateObject())
        {
            Assert.Matches(CamelCasePath, field.Name);
            Assert.Equal(JsonValueKind.Array, field.Value.ValueKind);
            Assert.NotEmpty(field.Value.EnumerateArray());
            Assert.All(field.Value.EnumerateArray(), message =>
            {
                Assert.Equal(JsonValueKind.String, message.ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(message.GetString()));
            });
        }

        foreach (var expected in expectedFields)
        {
            Assert.True(
                errors.TryGetProperty(expected, out _),
                $"Expected '{expected}' in errors, got: {string.Join(", ", errors.EnumerateObject().Select(p => p.Name))}");
        }

        return errors;
    }

    /// <summary>True when some error key matches the pattern (for per-item keys such as items[0].title).</summary>
    public static bool HasErrorKeyMatching(JsonElement errors, string pattern) =>
        errors.EnumerateObject().Any(p => Regex.IsMatch(p.Name, pattern));

    public static void AssertIsUtcTimestamp(JsonElement value)
    {
        var text = value.GetString();
        Assert.NotNull(text);
        Assert.EndsWith("Z", text);
        var parsed = DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    public static void AssertIsGuid(JsonElement value) =>
        Assert.True(
            Guid.TryParseExact(value.GetString(), "D", out var id) && id != Guid.Empty,
            $"'{value}' is not a canonical hyphenated GUID.");
}

/// <summary>
/// The real clock plus a settable offset. Login runs at offset zero (the JWT is validated against
/// the real clock), then a test moves "today" forward without touching the system clock.
/// </summary>
internal sealed class OffsetTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}
