using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

public sealed class CreateRequestTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HttpClient> StaffClientAsync()
    {
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();
        return client;
    }

    // ---- S1.1 -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_ValidBody_Returns201WithIdAndLocationHeader_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertIsGuid(body.GetProperty("id"));
        var id = body.GetProperty("id").GetString();

        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"/api/requests/{id}", response.Headers.Location.OriginalString);
    }

    [Fact]
    public async Task Create_ValidBody_PersistsRequestAndEveryItem_S1_1()
    {
        var client = await StaffClientAsync();
        var due = UtcToday.AddDays(30);

        var id = await client.CreateAsync(Body(
            clientName: "Persisted Client",
            clientEmail: "persisted@example.com",
            dueDate: due,
            items:
            [
                Item("Passport", "Photo page", required: true),
                Item("Payslip", null, required: false),
                Item("Proof of address", "Dated in the last 3 months", required: true),
            ]));

        var request = await factory.Services.RunDbAsync(db => db.DocumentRequests
            .AsNoTracking().Include(r => r.Items).SingleAsync(r => r.Id == id));

        Assert.Equal("Persisted Client", request.ClientName);
        Assert.Equal("persisted@example.com", request.ClientEmail);
        Assert.Equal(due, request.DueDate);
        Assert.Equal(await factory.Services.DemoStaffIdAsync(), request.CreatedBy);

        Assert.Equal(3, request.Items.Count);
        var passport = Assert.Single(request.Items, i => i.Title == "Passport");
        Assert.Equal("Photo page", passport.Description);
        Assert.True(passport.Required);
        var payslip = Assert.Single(request.Items, i => i.Title == "Payslip");
        Assert.Null(payslip.Description);
        Assert.False(payslip.Required);
        Assert.Contains(request.Items, i => i.Title == "Proof of address" && i.Required);
        Assert.All(request.Items, i => Assert.Equal(id, i.RequestId));
    }

    [Fact]
    public async Task Create_StoresCreatedAtAsTheCurrentUtcInstant_S10_3()
    {
        var client = await StaffClientAsync();
        var before = DateTime.UtcNow.AddMinutes(-1);

        var id = await client.CreateAsync();

        var request = await factory.Services.RunDbAsync(db =>
            db.DocumentRequests.AsNoTracking().SingleAsync(r => r.Id == id));
        Assert.InRange(request.CreatedAt, before, DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Create_ItemIdsAreGuids_S10_4()
    {
        var client = await StaffClientAsync();

        var id = await client.CreateAsync();

        var items = await factory.Services.RunDbAsync(db =>
            db.ChecklistItems.AsNoTracking().Where(i => i.RequestId == id).ToListAsync());
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.NotEqual(Guid.Empty, i.Id));
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
    }

    // ---- S1.5 -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_NewRequestIsDraftAndEveryItemIsOutstanding_S1_5()
    {
        var client = await StaffClientAsync();

        var id = await client.CreateAsync(Body(items: [Item("A"), Item("B", required: false), Item("C")]));

        var request = await factory.Services.RunDbAsync(db => db.DocumentRequests
            .AsNoTracking().Include(r => r.Items).SingleAsync(r => r.Id == id));

        Assert.Equal(RequestStatus.Draft, request.Status);
        Assert.Equal(3, request.Items.Count);
        Assert.All(request.Items, i => Assert.Equal(ItemStatus.Outstanding, i.Status));
    }

    [Fact]
    public async Task Create_ThenList_ShowsTheRequestAsDraftWithItemsOutstanding_S1_5()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync(Body(clientName: "Visible In List"));

        var page = await client.GetFromJsonAsync<JsonElement>("/api/requests?pageSize=100");
        var row = page.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == id);

        Assert.Equal("Draft", row.GetProperty("status").GetString());
        Assert.Equal(2, row.GetProperty("itemCount").GetInt32());
        Assert.Equal(0, row.GetProperty("approvedCount").GetInt32());
        Assert.False(row.GetProperty("isOverdue").GetBoolean());
        AssertIsUtcTimestamp(row.GetProperty("createdAt"));
    }

    // ---- S1.2 -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_ZeroItems_Returns400NamingItems_S1_2()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(items: []));

        await AssertValidationProblemAsync(response, "items");
    }

    [Fact]
    public async Task Create_NullOrMissingItems_Returns400NamingItems_S1_2()
    {
        var client = await StaffClientAsync();

        var nullItems = Body();
        nullItems["items"] = null;
        await AssertValidationProblemAsync(await client.PostRequestAsync(nullItems), "items");

        var missingItems = Body();
        missingItems.Remove("items");
        await AssertValidationProblemAsync(await client.PostRequestAsync(missingItems), "items");
    }

    [Fact]
    public async Task Create_FiftyItems_Succeeds_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(
            Body(items: Enumerable.Range(1, 50).Select(n => (object?)Item($"Document {n}"))));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_FiftyOneItems_Returns400NamingItems_S1_2()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(
            Body(items: Enumerable.Range(1, 51).Select(n => (object?)Item($"Document {n}"))));

        await AssertValidationProblemAsync(response, "items");
    }

    // ---- S1.3 -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_DueDateYesterday_Returns400NamingDueDate_S1_3()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(dueDate: UtcToday.AddDays(-1)));

        await AssertValidationProblemAsync(response, "dueDate");
    }

    [Fact]
    public async Task Create_DueDateLongAgo_Returns400NamingDueDate_S1_3()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(dueDate: new DateOnly(2001, 1, 1)));

        await AssertValidationProblemAsync(response, "dueDate");
    }

    [Fact]
    public async Task Create_DueDateToday_IsAccepted_S1_3()
    {
        // The contract says "today or later". Computed immediately before the call; the only way
        // this flakes is a run that straddles midnight UTC.
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(dueDate: UtcToday));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- S1.4 -----------------------------------------------------------------------------

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("jane@")]
    [InlineData("@example.com")]
    [InlineData("jane example@example.com")]
    [InlineData("")]
    public async Task Create_InvalidClientEmail_Returns400NamingClientEmail_S1_4(string email)
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(clientEmail: email));

        await AssertValidationProblemAsync(response, "clientEmail");
    }

    [Fact]
    public async Task Create_ClientEmailOver320Characters_Returns400NamingClientEmail_S1_4()
    {
        var client = await StaffClientAsync();
        var email = new string('a', 321 - "@example.com".Length) + "@example.com";
        Assert.Equal(321, email.Length);

        var response = await client.PostRequestAsync(Body(clientEmail: email));

        await AssertValidationProblemAsync(response, "clientEmail");
    }

    // ---- Field length limits (api-contract.md, "Requests (staff)") -------------------------

    [Fact]
    public async Task Create_ClientNameOf200Characters_Succeeds_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(clientName: new string('n', 200)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_ClientNameOf201Characters_Returns400NamingClientName_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(clientName: new string('n', 201)));

        await AssertValidationProblemAsync(response, "clientName");
    }

    [Fact]
    public async Task Create_EmptyClientName_Returns400NamingClientName_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(clientName: ""));

        await AssertValidationProblemAsync(response, "clientName");
    }

    [Fact]
    public async Task Create_ItemTitleOf200Characters_Succeeds_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(items: [Item(new string('t', 200))]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_ItemTitleOf201Characters_Returns400NamingTheItemTitle_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(items: [Item(new string('t', 201))]));

        var errors = await AssertValidationProblemAsync(response);
        Assert.True(HasErrorKeyMatching(errors, @"^items.*title$"),
            "Expected a key such as items[0].title in errors.");
    }

    [Fact]
    public async Task Create_EmptyItemTitle_Returns400NamingTheItemTitle_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(items: [Item("")]));

        var errors = await AssertValidationProblemAsync(response);
        Assert.True(HasErrorKeyMatching(errors, @"^items.*title$"),
            "Expected a key such as items[0].title in errors.");
    }

    [Fact]
    public async Task Create_ItemDescriptionOf1000Characters_Succeeds_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(
            Body(items: [Item(description: new string('d', 1000))]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_ItemDescriptionOf1001Characters_Returns400NamingTheItemDescription_S1_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(
            Body(items: [Item(description: new string('d', 1001))]));

        var errors = await AssertValidationProblemAsync(response);
        Assert.True(HasErrorKeyMatching(errors, @"^items.*description$"),
            "Expected a key such as items[0].description in errors.");
    }

    // ---- S10.1: every 400 has the shared shape ----------------------------------------------

    [Fact]
    public async Task Create_SeveralInvalidFieldsAtOnce_ReportsEveryFieldInOneResponse_S10_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostRequestAsync(Body(
            clientName: "",
            clientEmail: "nope",
            dueDate: UtcToday.AddDays(-3),
            items: []));

        await AssertValidationProblemAsync(response, "clientName", "clientEmail", "dueDate", "items");
    }

    [Fact]
    public async Task Create_EmptyJsonObject_Returns400InTheSharedShape_S10_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostAsync(
            "/api/requests", new StringContent("{}", Encoding.UTF8, "application/json"));

        await AssertValidationProblemAsync(response, "clientName", "clientEmail", "items");
    }

    [Fact]
    public async Task Create_UnparseableDueDate_Returns400InTheSharedProblemShape_S10_1()
    {
        // Not a listed field rule: the criterion is only that no 400 body strays from the contract,
        // including the ones the framework would otherwise write itself.
        var client = await StaffClientAsync();

        var response = await client.PostAsync(
            "/api/requests",
            new StringContent(
                """{"clientName":"Jane","clientEmail":"jane@example.com","dueDate":"next tuesday","items":[{"title":"x","description":null,"required":true}]}""",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ValidationType, root.GetProperty("type").GetString());
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Create_MalformedJson_Returns400InTheSharedProblemShape_S10_1()
    {
        var client = await StaffClientAsync();

        var response = await client.PostAsync(
            "/api/requests", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ValidationType, root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Create_InvalidBody_WritesNoRowsAndNoAuditEvent_S1_6()
    {
        var client = await StaffClientAsync();

        async Task<(int Requests, int Items, int Events)> CountsAsync() =>
            await factory.Services.RunDbAsync(async db =>
                (await db.DocumentRequests.CountAsync(),
                 await db.ChecklistItems.CountAsync(),
                 await db.AuditEvents.CountAsync(e => e.Action == AuditAction.RequestCreated)));

        var before = await CountsAsync();

        await AssertValidationProblemAsync(await client.PostRequestAsync(Body(items: [])), "items");
        await AssertValidationProblemAsync(await client.PostRequestAsync(Body(clientEmail: "nope")), "clientEmail");
        await AssertValidationProblemAsync(await client.PostRequestAsync(Body(dueDate: UtcToday.AddDays(-1))), "dueDate");

        Assert.Equal(before, await CountsAsync());
    }

    // ---- S1.6 (happy path; the atomicity proofs are in RequestAtomicityTests) ----------------

    [Fact]
    public async Task Create_WritesExactlyOneRequestCreatedAuditEventForTheStaffUser_S1_6()
    {
        var client = factory.CreateClient();
        var jwt = await client.AuthenticateAsStaffAsync();
        var staffId = await factory.Services.DemoStaffIdAsync();

        var id = await client.CreateAsync();

        var events = await factory.Services.RunDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == id)
            .ToListAsync());

        var audit = Assert.Single(events);
        Assert.Equal(AuditAction.RequestCreated, audit.Action);
        Assert.Equal(ActorType.Staff, audit.ActorType);
        Assert.Equal(staffId, audit.ActorId);
        Assert.Equal(nameof(DocumentRequest), audit.EntityType);
        Assert.Equal(id, audit.EntityId);
        Assert.InRange(audit.OccurredAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        // Metadata is optional, but whatever is there must never carry a credential (S6.6).
        if (audit.Metadata is not null)
        {
            Assert.DoesNotContain(jwt, audit.Metadata);
            Assert.DoesNotContain("eyJ", audit.Metadata);
            Assert.DoesNotContain("Bearer", audit.Metadata, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiFactory.DemoPassword, audit.Metadata);
        }
    }

    [Fact]
    public async Task Create_ActorIsTakenFromTheJwtNotTheBody_S1_6()
    {
        var client = await StaffClientAsync();
        var body = Body();
        body["actorId"] = Guid.NewGuid();
        body["createdBy"] = Guid.NewGuid();

        var id = await client.CreateAsync(body);

        var staffId = await factory.Services.DemoStaffIdAsync();
        var request = await factory.Services.RunDbAsync(db =>
            db.DocumentRequests.AsNoTracking().SingleAsync(r => r.Id == id));
        var audit = await factory.Services.RunDbAsync(db =>
            db.AuditEvents.AsNoTracking().SingleAsync(e => e.EntityId == id));

        Assert.Equal(staffId, request.CreatedBy);
        Assert.Equal(staffId, audit.ActorId);
    }

    // ---- S1.7 (create) --------------------------------------------------------------------

    [Fact]
    public async Task Create_WithoutJwt_Returns401_S1_7()
    {
        var response = await factory.CreateClient().PostRequestAsync(Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutJwtAndAnInvalidBody_Returns401NotValidation400_S1_7()
    {
        var response = await factory.CreateClient().PostRequestAsync(Body(items: []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithGarbageBearerToken_Returns401_S1_7()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");

        var response = await client.PostRequestAsync(Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithPortalStyleCredential_Returns401_S1_7()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Portal some-portal-token");

        var response = await client.PostRequestAsync(Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutJwt_CreatesNothing_S1_7()
    {
        var before = await factory.Services.RunDbAsync(db => db.DocumentRequests.CountAsync());

        var response = await factory.CreateClient().PostRequestAsync(Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await factory.Services.RunDbAsync(db => db.DocumentRequests.CountAsync()));
    }
}
