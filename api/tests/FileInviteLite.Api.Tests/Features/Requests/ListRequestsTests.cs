using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// Tests run serially within a class, so each one empties the request tables first and then seeds
/// exactly the rows it needs: totals and page contents can be asserted exactly.
/// </summary>
public sealed class ListRequestsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HttpClient> StaffClientAsync()
    {
        await factory.Services.ResetRequestsAsync();
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();
        return client;
    }

    private static async Task<JsonElement> GetPageAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/requests" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

    // ---- S1.8: row shape -------------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsEveryDocumentedFieldAndTheEnvelope_S1_8()
    {
        var client = await StaffClientAsync();
        var due = UtcToday.AddDays(10);
        var id = await factory.Services.SeedAsync(
            due, RequestStatus.InProgress, itemCount: 4, approvedCount: 2, clientName: "Jane Client");

        var page = await GetPageAsync(client);

        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, page.GetProperty("total").GetInt32());

        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.Equal("Jane Client", row.GetProperty("clientName").GetString());
        Assert.Equal(due.ToString("yyyy-MM-dd"), row.GetProperty("dueDate").GetString());
        Assert.Equal("InProgress", row.GetProperty("status").GetString());
        Assert.Equal(4, row.GetProperty("itemCount").GetInt32());
        Assert.Equal(2, row.GetProperty("approvedCount").GetInt32());
        Assert.Equal(JsonValueKind.False, row.GetProperty("isOverdue").ValueKind);
        Assert.Equal(JsonValueKind.String, row.GetProperty("createdAt").ValueKind);
    }

    [Fact]
    public async Task List_ItemAndApprovedCountsAreCorrectPerRow_S1_8()
    {
        var client = await StaffClientAsync();
        var three = await factory.Services.SeedAsync(UtcToday.AddDays(1), itemCount: 3, approvedCount: 1);
        var one = await factory.Services.SeedAsync(UtcToday.AddDays(2), itemCount: 1, approvedCount: 0);
        var five = await factory.Services.SeedAsync(UtcToday.AddDays(3), itemCount: 5, approvedCount: 5);

        var rows = (await GetPageAsync(client)).GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetGuid());

        Assert.Equal((3, 1), (rows[three].GetProperty("itemCount").GetInt32(), rows[three].GetProperty("approvedCount").GetInt32()));
        Assert.Equal((1, 0), (rows[one].GetProperty("itemCount").GetInt32(), rows[one].GetProperty("approvedCount").GetInt32()));
        Assert.Equal((5, 5), (rows[five].GetProperty("itemCount").GetInt32(), rows[five].GetProperty("approvedCount").GetInt32()));
    }

    [Fact]
    public async Task List_IdsAreGuidsAndTimestampsAreUtcWithZ_S10_3_S10_4()
    {
        var client = await StaffClientAsync();
        await factory.Services.SeedAsync(UtcToday.AddDays(1));

        var page = await GetPageAsync(client);

        foreach (var row in page.GetProperty("items").EnumerateArray())
        {
            AssertIsGuid(row.GetProperty("id"));
            AssertIsUtcTimestamp(row.GetProperty("createdAt"));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", row.GetProperty("dueDate").GetString()!);
        }
    }

    [Fact]
    public async Task List_EmptyDatabase_ReturnsEmptyItemsAndZeroTotal_S1_8()
    {
        var client = await StaffClientAsync();

        var page = await GetPageAsync(client);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(0, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
    }

    // ---- S1.9: sorting ---------------------------------------------------------------------

    [Fact]
    public async Task List_DefaultSort_IsDueDateAscending_S1_9()
    {
        var client = await StaffClientAsync();
        var later = await factory.Services.SeedAsync(UtcToday.AddDays(30));
        var earliest = await factory.Services.SeedAsync(UtcToday.AddDays(2));
        var middle = await factory.Services.SeedAsync(UtcToday.AddDays(10));
        var overdue = await factory.Services.SeedAsync(UtcToday.AddDays(-5));

        var ids = Ids(await GetPageAsync(client));

        Assert.Equal([overdue, earliest, middle, later], ids);
    }

    [Fact]
    public async Task List_ExplicitSortDueDate_IsDueDateAscending_S1_9()
    {
        var client = await StaffClientAsync();
        var later = await factory.Services.SeedAsync(UtcToday.AddDays(30));
        var earlier = await factory.Services.SeedAsync(UtcToday.AddDays(2));

        var ids = Ids(await GetPageAsync(client, "?sort=dueDate"));

        Assert.Equal([earlier, later], ids);
    }

    [Fact]
    public async Task List_SortCreatedAt_IsHonouredAndAscending_S1_9()
    {
        // Created order is the reverse of due-date order, so the two sorts disagree on every pair.
        var client = await StaffClientAsync();
        var now = DateTime.UtcNow;
        var createdFirst = await factory.Services.SeedAsync(
            UtcToday.AddDays(30), createdAt: now.AddHours(-3));
        var createdSecond = await factory.Services.SeedAsync(
            UtcToday.AddDays(20), createdAt: now.AddHours(-2));
        var createdThird = await factory.Services.SeedAsync(
            UtcToday.AddDays(10), createdAt: now.AddHours(-1));

        var byCreated = Ids(await GetPageAsync(client, "?sort=createdAt"));
        var byDefault = Ids(await GetPageAsync(client));

        Assert.Equal([createdFirst, createdSecond, createdThird], byCreated);
        Assert.Equal([createdThird, createdSecond, createdFirst], byDefault);
    }

    // ---- S1.9: status filter ---------------------------------------------------------------

    [Theory]
    [InlineData("Draft")]
    [InlineData("Sent")]
    [InlineData("InProgress")]
    [InlineData("Complete")]
    public async Task List_FilterByStatus_ReturnsOnlyThatStatusAndTotalMatches_S1_9(string status)
    {
        var client = await StaffClientAsync();
        var expected = new Dictionary<string, Guid>();
        foreach (var s in Enum.GetValues<RequestStatus>())
        {
            // Two of each, so the filter is shown to return more than one row and not just a match.
            var a = await factory.Services.SeedAsync(UtcToday.AddDays(5), s);
            var b = await factory.Services.SeedAsync(UtcToday.AddDays(6), s);
            expected[$"{s}-a"] = a;
            expected[$"{s}-b"] = b;
        }

        var page = await GetPageAsync(client, $"?status={status}");

        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal([expected[$"{status}-a"], expected[$"{status}-b"]], Ids(page));
        Assert.All(page.GetProperty("items").EnumerateArray(),
            r => Assert.Equal(status, r.GetProperty("status").GetString()));
    }

    // ---- S1.9: overdue filter (HTTP level) ---------------------------------------------------

    private sealed record OverdueFixture(
        Guid OverdueDraft, Guid OverdueSent, Guid OverdueInProgress,
        Guid CompleteButPastDue, Guid DueToday, Guid DueTomorrow, Guid DueNextMonth);

    private async Task<OverdueFixture> SeedOverdueMixAsync()
    {
        var today = UtcToday;
        return new OverdueFixture(
            OverdueDraft: await factory.Services.SeedAsync(today.AddDays(-10), RequestStatus.Draft),
            OverdueSent: await factory.Services.SeedAsync(today.AddDays(-3), RequestStatus.Sent),
            OverdueInProgress: await factory.Services.SeedAsync(today.AddDays(-1), RequestStatus.InProgress),
            CompleteButPastDue: await factory.Services.SeedAsync(
                today.AddDays(-20), RequestStatus.Complete, itemCount: 2, approvedCount: 2),
            DueToday: await factory.Services.SeedAsync(today, RequestStatus.Sent),
            DueTomorrow: await factory.Services.SeedAsync(today.AddDays(1), RequestStatus.Draft),
            DueNextMonth: await factory.Services.SeedAsync(today.AddDays(30), RequestStatus.Draft));
    }

    [Fact]
    public async Task List_OverdueTrue_ReturnsOnlyPastDueNonCompleteRequests_S1_9()
    {
        var client = await StaffClientAsync();
        var seeded = await SeedOverdueMixAsync();

        var page = await GetPageAsync(client, "?overdue=true");

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal([seeded.OverdueDraft, seeded.OverdueSent, seeded.OverdueInProgress], Ids(page));
        Assert.All(page.GetProperty("items").EnumerateArray(),
            r => Assert.True(r.GetProperty("isOverdue").GetBoolean()));
    }

    [Fact]
    public async Task List_OverdueFalse_ReturnsEverythingElse_S1_9()
    {
        var client = await StaffClientAsync();
        var seeded = await SeedOverdueMixAsync();

        var page = await GetPageAsync(client, "?overdue=false");

        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal(
            [seeded.CompleteButPastDue, seeded.DueToday, seeded.DueTomorrow, seeded.DueNextMonth],
            Ids(page));
        Assert.All(page.GetProperty("items").EnumerateArray(),
            r => Assert.False(r.GetProperty("isOverdue").GetBoolean()));
    }

    [Fact]
    public async Task List_WithoutOverdueFilter_FlagsEachRowCorrectly_S1_8()
    {
        var client = await StaffClientAsync();
        var seeded = await SeedOverdueMixAsync();

        var rows = (await GetPageAsync(client)).GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("isOverdue").GetBoolean());

        Assert.Equal(7, rows.Count);
        Assert.True(rows[seeded.OverdueDraft]);
        Assert.True(rows[seeded.OverdueSent]);
        Assert.True(rows[seeded.OverdueInProgress]);
        Assert.False(rows[seeded.CompleteButPastDue]);   // past due, but Complete is never overdue
        Assert.False(rows[seeded.DueToday]);             // due today is not yet overdue
        Assert.False(rows[seeded.DueTomorrow]);
        Assert.False(rows[seeded.DueNextMonth]);
    }

    [Fact]
    public async Task List_OverdueCombinedWithStatus_AppliesBothFilters_S1_9()
    {
        var client = await StaffClientAsync();
        var seeded = await SeedOverdueMixAsync();

        var page = await GetPageAsync(client, "?overdue=true&status=Sent");

        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal([seeded.OverdueSent], Ids(page));
    }

    [Fact]
    public async Task List_OverdueFilterIsAppliedBeforePaging_SoTotalAndPagesReflectTheFilter_S1_9()
    {
        // 25 overdue rows among 15 that are not. If the filter ran after the page was cut, or in
        // memory over the first page only, page 3 would be empty and total would be wrong.
        var client = await StaffClientAsync();
        for (var i = 0; i < 25; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(-30 + i), RequestStatus.Sent);
        }

        for (var i = 0; i < 15; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(1 + i), RequestStatus.Sent);
        }

        var third = await GetPageAsync(client, "?overdue=true&pageSize=10&page=3");

        Assert.Equal(25, third.GetProperty("total").GetInt32());
        Assert.Equal(3, third.GetProperty("page").GetInt32());
        Assert.Equal(10, third.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, Ids(third).Count);
        Assert.All(third.GetProperty("items").EnumerateArray(),
            r => Assert.True(r.GetProperty("isOverdue").GetBoolean()));
    }

    [Fact]
    public async Task List_TodayIsTakenFromTheRegisteredTimeProvider_S1_8()
    {
        // A request due in 5 days is not overdue now, and is overdue once the clock reads 10 days
        // later. Login happens at offset zero because the JWT is validated against the real clock.
        using var baseFactory = new ApiFactory();
        var clock = new OffsetTimeProvider();
        using var shifted = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));

        var client = shifted.CreateClient();
        await client.AuthenticateAsStaffAsync();
        var id = await shifted.Services.SeedAsync(UtcToday.AddDays(5), RequestStatus.Sent);

        var beforeShift = await GetPageAsync(client, "?overdue=true");
        Assert.Equal(0, beforeShift.GetProperty("total").GetInt32());

        clock.Offset = TimeSpan.FromDays(10);

        var afterShift = await GetPageAsync(client, "?overdue=true");
        Assert.Equal(1, afterShift.GetProperty("total").GetInt32());
        var row = Assert.Single(afterShift.GetProperty("items").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.True(row.GetProperty("isOverdue").GetBoolean());
    }

    // ---- Pagination ----------------------------------------------------------------------------

    [Fact]
    public async Task List_DefaultsToPage1OfTwentyRows_AndTotalCountsAllRows()
    {
        var client = await StaffClientAsync();
        for (var i = 0; i < 25; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(1 + i));
        }

        var page = await GetPageAsync(client);

        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(25, page.GetProperty("total").GetInt32());
        Assert.Equal(20, Ids(page).Count);
    }

    [Fact]
    public async Task List_SecondPage_ReturnsTheRemainingRows()
    {
        var client = await StaffClientAsync();
        for (var i = 0; i < 25; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(1 + i));
        }

        var page = await GetPageAsync(client, "?page=2");

        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(25, page.GetProperty("total").GetInt32());
        Assert.Equal(5, Ids(page).Count);
    }

    [Fact]
    public async Task List_PageBeyondTheEnd_Returns200WithNoRowsAndTheRealTotal()
    {
        var client = await StaffClientAsync();
        for (var i = 0; i < 3; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(1 + i));
        }

        var page = await GetPageAsync(client, "?page=9");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(9, page.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task List_PagesAreDisjointAndTogetherCompleteEvenWhenDueDatesTie()
    {
        // 25 rows over 5 distinct due dates: ordering by due date alone is ambiguous, so paging is
        // only correct if the order is made deterministic.
        var client = await StaffClientAsync();
        var seeded = new HashSet<Guid>();
        for (var i = 0; i < 25; i++)
        {
            seeded.Add(await factory.Services.SeedAsync(UtcToday.AddDays(1 + (i % 5))));
        }

        var seen = new List<Guid>();
        for (var p = 1; p <= 5; p++)
        {
            seen.AddRange(Ids(await GetPageAsync(client, $"?page={p}&pageSize=5")));
        }

        Assert.Equal(25, seen.Count);
        Assert.Equal(seeded, seen.ToHashSet());
    }

    [Fact]
    public async Task List_CustomPageSize_IsHonouredAndEchoed()
    {
        var client = await StaffClientAsync();
        for (var i = 0; i < 12; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(1 + i));
        }

        var page = await GetPageAsync(client, "?pageSize=5");

        Assert.Equal(5, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, Ids(page).Count);
        Assert.Equal(12, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task List_PageSizeOf100_IsTheAllowedMaximum()
    {
        var client = await StaffClientAsync();
        await factory.Services.SeedAsync(UtcToday.AddDays(1));

        var page = await GetPageAsync(client, "?pageSize=100");

        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task List_PageSizeOf101_Returns400NamingPageSize()
    {
        var client = await StaffClientAsync();

        var response = await client.GetAsync("/api/requests?pageSize=101");

        await AssertValidationProblemAsync(response, "pageSize");
    }

    [Fact]
    public async Task List_TotalReflectsTheFiltersNotThePage()
    {
        var client = await StaffClientAsync();
        for (var i = 0; i < 30; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(1 + i), RequestStatus.Draft);
        }

        for (var i = 0; i < 5; i++)
        {
            await factory.Services.SeedAsync(UtcToday.AddDays(1 + i), RequestStatus.Sent);
        }

        var sent = await GetPageAsync(client, "?status=Sent&pageSize=2");
        Assert.Equal(2, Ids(sent).Count);
        Assert.Equal(5, sent.GetProperty("total").GetInt32());

        var draftPage3 = await GetPageAsync(client, "?status=Draft&pageSize=10&page=3");
        Assert.Equal(10, Ids(draftPage3).Count);
        Assert.Equal(30, draftPage3.GetProperty("total").GetInt32());

        var unfiltered = await GetPageAsync(client, "?pageSize=1");
        Assert.Equal(35, unfiltered.GetProperty("total").GetInt32());
    }

    // ---- S10.1: bad query values -------------------------------------------------------------

    [Theory]
    [InlineData("status=bogus", "status")]
    [InlineData("overdue=maybe", "overdue")]
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("page=abc", "page")]
    [InlineData("pageSize=abc", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=-5", "pageSize")]
    [InlineData("sort=bogus", "sort")]
    public async Task List_BadQueryValue_Returns400InTheSharedShapeNamingTheParameter_S10_1(
        string query, string field)
    {
        var client = await StaffClientAsync();

        var response = await client.GetAsync($"/api/requests?{query}");

        await AssertValidationProblemAsync(response, field);
    }

    [Fact]
    public async Task List_SeveralBadQueryValues_ReportsEachInOneResponse_S10_1()
    {
        var client = await StaffClientAsync();

        var response = await client.GetAsync("/api/requests?status=bogus&page=0&pageSize=101");

        await AssertValidationProblemAsync(response, "status", "page", "pageSize");
    }

    // ---- S1.7 (list) ---------------------------------------------------------------------------

    [Fact]
    public async Task List_WithoutJwt_Returns401_S1_7()
    {
        await factory.Services.ResetRequestsAsync();
        await factory.Services.SeedAsync(UtcToday.AddDays(1));

        var response = await factory.CreateClient().GetAsync("/api/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutJwtAndABadQuery_Returns401NotValidation400_S1_7()
    {
        var response = await factory.CreateClient().GetAsync("/api/requests?pageSize=101&status=bogus");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithGarbageBearerToken_Returns401_S1_7()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");

        var response = await client.GetAsync("/api/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_WithPortalStyleCredential_Returns401_S1_7()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Portal some-portal-token");

        var response = await client.GetAsync("/api/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_Unauthenticated_NeverReturnsRequestData_S1_7()
    {
        await factory.Services.ResetRequestsAsync();
        await factory.Services.SeedAsync(UtcToday.AddDays(1), clientName: "Secret Client Name");

        var response = await factory.CreateClient().GetAsync("/api/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Secret Client Name", await response.Content.ReadAsStringAsync());
    }
}
