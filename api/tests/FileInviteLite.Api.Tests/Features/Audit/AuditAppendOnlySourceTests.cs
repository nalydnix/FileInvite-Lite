using System.Text.RegularExpressions;

namespace FileInviteLite.Api.Tests.Features.Audit;

/// <summary>
/// S6.3 is a negative — no application code updates or deletes an audit event — and a negative
/// can only be pinned by reading the source. Unusual for a test, deliberate here.
/// </summary>
public sealed partial class AuditAppendOnlySourceTests
{
    // Any member that mutates or removes rows, called on the AuditEvents set (or Set<AuditEvent>()),
    // directly or at the end of a query chain.
    [GeneratedRegex(@"(AuditEvents|Set<\s*AuditEvent\s*>\s*\(\s*\))\s*(\.\s*(Remove|RemoveRange|Update|UpdateRange|ExecuteDelete|ExecuteDeleteAsync|ExecuteUpdate|ExecuteUpdateAsync)\b|\.\s*(Where|AsNoTracking|AsQueryable|IgnoreQueryFilters)[^;]*\.\s*(ExecuteDelete|ExecuteDeleteAsync|ExecuteUpdate|ExecuteUpdateAsync)\b)", RegexOptions.Singleline)]
    private static partial Regex Mutation();

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "api", "src", "FileInviteLite.Api")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new DirectoryNotFoundException("Could not find api/src from the test output folder.")
            : Path.Combine(dir.FullName, "api", "src", "FileInviteLite.Api");
    }

    [Fact]
    public void NoApplicationCodeUpdatesOrDeletesAnAuditEvent_S6_3()
    {
        var files = Directory.GetFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // Guards against the scan silently covering nothing.
        Assert.Contains(files, f => f.EndsWith("AuditWriter.cs"));

        var offenders = files
            .Where(f => Mutation().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("db.AuditEvents.Remove(e);")]
    [InlineData("db.AuditEvents.RemoveRange(all);")]
    [InlineData("db.AuditEvents.Update(e);")]
    [InlineData("await db.AuditEvents.ExecuteDeleteAsync();")]
    [InlineData("await db.AuditEvents.Where(e => e.Id == id).ExecuteUpdateAsync(s => s);")]
    [InlineData("db.Set<AuditEvent>().Remove(e);")]
    public void TheScanRecognisesEachForbiddenForm_S6_3(string line) =>
        Assert.Matches(Mutation(), line);

    [Theory]
    [InlineData("db.AuditEvents.Add(e);")]
    [InlineData("db.AuditEvents.AsNoTracking().Where(e => e.Id == id).CountAsync();")]
    [InlineData("db.ChecklistItems.ExecuteDeleteAsync();")]
    public void TheScanIgnoresReadsAndAdds_S6_3(string line) =>
        Assert.DoesNotMatch(Mutation(), line);
}
