using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// The two derived rules from docs/spec.md ("Status roll-up rule"), which live on the domain
/// entity so no handler restates them. The roll-up tests are plain unit tests; the overdue tests
/// include a run against a real SQLite file to prove the predicate translates to SQL.
/// </summary>
public sealed class DocumentRequestRulesTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static DocumentRequest RequestWith(params (bool Required, ItemStatus Status)[] items)
    {
        var request = new DocumentRequest
        {
            ClientName = "Unit Test",
            ClientEmail = "unit@example.com",
            DueDate = UtcToday.AddDays(7),
            CreatedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        };

        foreach (var (required, status) in items)
        {
            request.Items.Add(new ChecklistItem
            {
                RequestId = request.Id,
                Title = "Item",
                Required = required,
                Status = status,
            });
        }

        return request;
    }

    // ---- Status roll-up rule ---------------------------------------------------------------

    [Fact]
    public void RollUp_AllItemsOutstandingAndNoLink_IsDraft_S1_5()
    {
        var request = RequestWith((true, ItemStatus.Outstanding), (false, ItemStatus.Outstanding));

        Assert.Equal(RequestStatus.Draft, request.RollUpStatus(magicLinkIssued: false));
    }

    [Fact]
    public void RollUp_AllItemsOutstandingAndLinkIssued_IsSent()
    {
        var request = RequestWith((true, ItemStatus.Outstanding), (false, ItemStatus.Outstanding));

        Assert.Equal(RequestStatus.Sent, request.RollUpStatus(magicLinkIssued: true));
    }

    [Theory]
    [InlineData(ItemStatus.Uploaded)]
    [InlineData(ItemStatus.Rejected)]
    [InlineData(ItemStatus.Approved)]
    public void RollUp_AnyItemNotOutstanding_IsInProgressWhetherOrNotALinkWasIssued(ItemStatus touched)
    {
        // A second required item stays Outstanding so Complete is not reachable.
        var request = RequestWith((true, touched), (true, ItemStatus.Outstanding));

        Assert.Equal(RequestStatus.InProgress, request.RollUpStatus(magicLinkIssued: false));
        Assert.Equal(RequestStatus.InProgress, request.RollUpStatus(magicLinkIssued: true));
    }

    [Fact]
    public void RollUp_AllRequiredItemsApproved_IsComplete()
    {
        var request = RequestWith((true, ItemStatus.Approved), (true, ItemStatus.Approved));

        Assert.Equal(RequestStatus.Complete, request.RollUpStatus(magicLinkIssued: true));
        Assert.Equal(RequestStatus.Complete, request.RollUpStatus(magicLinkIssued: false));
    }

    [Theory]
    [InlineData(ItemStatus.Outstanding)]
    [InlineData(ItemStatus.Uploaded)]
    [InlineData(ItemStatus.Rejected)]
    public void RollUp_OptionalItemsDoNotBlockCompletion(ItemStatus optionalStatus)
    {
        var request = RequestWith((true, ItemStatus.Approved), (false, optionalStatus));

        Assert.Equal(RequestStatus.Complete, request.RollUpStatus(magicLinkIssued: true));
    }

    [Theory]
    [InlineData(ItemStatus.Outstanding)]
    [InlineData(ItemStatus.Uploaded)]
    [InlineData(ItemStatus.Rejected)]
    public void RollUp_OneRequiredItemNotApproved_IsNotComplete(ItemStatus lagging)
    {
        var request = RequestWith(
            (true, ItemStatus.Approved), (true, ItemStatus.Approved), (true, lagging));

        Assert.NotEqual(RequestStatus.Complete, request.RollUpStatus(magicLinkIssued: true));
    }

    [Fact]
    public void RollUp_ApprovedRequiredItemWithAnOutstandingRequiredOne_IsInProgress()
    {
        var request = RequestWith((true, ItemStatus.Approved), (true, ItemStatus.Outstanding));

        Assert.Equal(RequestStatus.InProgress, request.RollUpStatus(magicLinkIssued: true));
    }

    [Fact]
    public void RollUp_NoRequiredItemsAndNothingTouched_IsNeverComplete()
    {
        var onlyOptional = RequestWith((false, ItemStatus.Outstanding), (false, ItemStatus.Outstanding));
        var noItems = RequestWith();

        Assert.Equal(RequestStatus.Draft, onlyOptional.RollUpStatus(magicLinkIssued: false));
        Assert.Equal(RequestStatus.Sent, onlyOptional.RollUpStatus(magicLinkIssued: true));
        Assert.Equal(RequestStatus.Draft, noItems.RollUpStatus(magicLinkIssued: false));
        Assert.NotEqual(RequestStatus.Complete, noItems.RollUpStatus(magicLinkIssued: true));
    }

    [Fact]
    public void RollUp_OnlyOptionalItemsAllApproved_IsInProgressNotComplete()
    {
        // "All required items approved" would be vacuously true here; the rule must not read it so.
        var request = RequestWith((false, ItemStatus.Approved), (false, ItemStatus.Approved));

        Assert.Equal(RequestStatus.InProgress, request.RollUpStatus(magicLinkIssued: true));
    }

    // ---- Overdue rule ----------------------------------------------------------------------

    [Theory]
    [InlineData(RequestStatus.Draft)]
    [InlineData(RequestStatus.Sent)]
    [InlineData(RequestStatus.InProgress)]
    public void IsOverdue_DueBeforeTodayAndNotComplete_IsTrue(RequestStatus status)
    {
        var today = new DateOnly(2026, 6, 15);
        var request = RequestWith();
        request.DueDate = today.AddDays(-1);
        request.Status = status;

        Assert.True(DocumentRequest.IsOverdue(today).Compile()(request));
    }

    [Fact]
    public void IsOverdue_DueBeforeTodayButComplete_IsFalse()
    {
        var today = new DateOnly(2026, 6, 15);
        var request = RequestWith();
        request.DueDate = today.AddDays(-30);
        request.Status = RequestStatus.Complete;

        Assert.False(DocumentRequest.IsOverdue(today).Compile()(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(90)]
    public void IsOverdue_DueTodayOrLater_IsFalse(int daysFromToday)
    {
        var today = new DateOnly(2026, 6, 15);
        var request = RequestWith();
        request.DueDate = today.AddDays(daysFromToday);
        request.Status = RequestStatus.Sent;

        Assert.False(DocumentRequest.IsOverdue(today).Compile()(request));
    }

    [Fact]
    public async Task IsOverdue_TranslatesToSqlAndRunsAgainstARealSqliteFile_S1_9()
    {
        // EF throws InvalidOperationException for a predicate it cannot translate, and a
        // client-evaluated filter would never reach SQL at all. So: execute it, and read the SQL.
        await factory.Services.ResetRequestsAsync();
        var today = UtcToday;
        var overdue = await factory.Services.SeedAsync(today.AddDays(-4), RequestStatus.Sent);
        await factory.Services.SeedAsync(today, RequestStatus.Sent);
        await factory.Services.SeedAsync(today.AddDays(4), RequestStatus.Draft);
        await factory.Services.SeedAsync(today.AddDays(-9), RequestStatus.Complete, itemCount: 1, approvedCount: 1);

        var (found, sql) = await factory.Services.RunDbAsync(async db =>
        {
            var query = db.DocumentRequests.AsNoTracking().Where(DocumentRequest.IsOverdue(today));
            return (await query.Select(r => r.Id).ToListAsync(), query.ToQueryString());
        });

        Assert.Equal([overdue], found);
        Assert.Matches(@"""DueDate""\s*<", sql);
        Assert.Matches(@"""Status""\s*(<>|!=)", sql);
    }

    [Fact]
    public async Task IsOverdue_ComposesWithCountingAndPagingInOneSqlQuery_S1_9()
    {
        // The list endpoint needs Count + Skip/Take over the same predicate. Both must translate.
        await factory.Services.ResetRequestsAsync();
        var today = UtcToday;
        for (var i = 0; i < 12; i++)
        {
            await factory.Services.SeedAsync(today.AddDays(-1 - i), RequestStatus.Sent);
        }

        await factory.Services.SeedAsync(today.AddDays(3), RequestStatus.Sent);

        var (total, pageSql, pageIds) = await factory.Services.RunDbAsync(async db =>
        {
            var query = db.DocumentRequests.AsNoTracking().Where(DocumentRequest.IsOverdue(today));
            var page = query.OrderBy(r => r.DueDate).Skip(10).Take(10);
            return (await query.CountAsync(), page.ToQueryString(), await page.Select(r => r.Id).ToListAsync());
        });

        Assert.Equal(12, total);
        Assert.Equal(2, pageIds.Count);
        Assert.Matches(@"""DueDate""\s*<", pageSql);
        Assert.Contains("LIMIT", pageSql, StringComparison.OrdinalIgnoreCase);
    }
}
