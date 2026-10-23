using System.Net;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// S1.6: the request and its audit event commit together or not at all. Each test forces a failure
/// at one half of the write with a SQLite trigger and then checks the other half is absent.
/// </summary>
/// <remarks>
/// Every test builds its own <see cref="ApiFactory"/> and disposes it, so a trigger can never leak
/// into another test's database. The trigger is created after the host has started (migrations run
/// at startup) and after login (which writes its own audit event).
/// </remarks>
public sealed class RequestAtomicityTests
{
    private static Task<int> ExecAsync(ApiFactory factory, string sql) =>
        factory.Services.RunDbAsync(db => db.Database.ExecuteSqlRawAsync(sql));

    private static async Task AssertNothingWrittenAsync(ApiFactory factory)
    {
        var counts = await factory.Services.RunDbAsync(async db =>
            (Requests: await db.DocumentRequests.CountAsync(),
             Items: await db.ChecklistItems.CountAsync(),
             Events: await db.AuditEvents.CountAsync(e => e.Action == AuditAction.RequestCreated)));

        Assert.Equal(0, counts.Requests);
        Assert.Equal(0, counts.Items);
        Assert.Equal(0, counts.Events);
    }

    private static async Task AssertServerErrorWithoutDetailAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.False(root.TryGetProperty("errors", out _));

        // The contract: a 500 "carries only traceId" — never exception text.
        Assert.DoesNotContain("forced", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trigger", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WhenTheRequestInsertFails_LeavesNoAuditEventBehind_S1_6()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();

        await ExecAsync(factory, """
            CREATE TRIGGER fail_request_insert BEFORE INSERT ON "DocumentRequests"
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);

        var response = await client.PostRequestAsync(Body());

        await AssertServerErrorWithoutDetailAsync(response);
        await AssertNothingWrittenAsync(factory);
    }

    [Fact]
    public async Task Create_WhenTheAuditInsertFails_LeavesNoRequestOrItemsBehind_S1_6()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();

        // Scoped to RequestCreated so nothing else that audits is disturbed.
        await ExecAsync(factory, """
            CREATE TRIGGER fail_audit_insert BEFORE INSERT ON "AuditEvents"
            WHEN NEW."Action" = 'RequestCreated'
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);

        var response = await client.PostRequestAsync(Body());

        await AssertServerErrorWithoutDetailAsync(response);
        await AssertNothingWrittenAsync(factory);
    }

    [Fact]
    public async Task Create_WhenAnItemInsertFails_LeavesNoRequestOrAuditEventBehind_S1_6()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();

        await ExecAsync(factory, """
            CREATE TRIGGER fail_item_insert BEFORE INSERT ON "ChecklistItems"
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);

        var response = await client.PostRequestAsync(Body());

        await AssertServerErrorWithoutDetailAsync(response);
        await AssertNothingWrittenAsync(factory);
    }

    [Fact]
    public async Task Create_AfterARolledBackAttempt_ASubsequentCreateSucceedsWithOneAuditEvent_S1_6()
    {
        // Proves the failure left the connection and the database usable, and that the rollback is
        // real rather than the trigger simply being checked before any write happened.
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();

        await ExecAsync(factory, """
            CREATE TRIGGER fail_audit_insert BEFORE INSERT ON "AuditEvents"
            WHEN NEW."Action" = 'RequestCreated'
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);
        var failed = await client.PostRequestAsync(Body());
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        await ExecAsync(factory, """DROP TRIGGER fail_audit_insert;""");
        var id = await client.CreateAsync();

        var counts = await factory.Services.RunDbAsync(async db =>
            (Requests: await db.DocumentRequests.CountAsync(),
             Events: await db.AuditEvents.CountAsync(e => e.Action == AuditAction.RequestCreated)));
        Assert.Equal(1, counts.Requests);
        Assert.Equal(1, counts.Events);
        Assert.NotEqual(Guid.Empty, id);
    }
}
