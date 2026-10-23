using FileInviteLite.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileInviteLite.Api.Tests.Infrastructure;

/// <summary>
/// A host for integration tests, over a real HTTP pipeline and a real SQLite file.
/// </summary>
/// <remarks>
/// A file, not the EF in-memory provider: the SQLite provider's query translation is part of what
/// these tests exist to check, and in-memory would happily pass queries that fail in production
/// (ADR 0001). Each instance gets its own database and storage folder, so test classes run in
/// parallel without contending.
/// </remarks>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fileinvite-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The seeded demo user's credentials for this host.</summary>
    public const string DemoEmail = "demo@test.local";
    public const string DemoPassword = "correct-horse-battery-staple";

    /// <summary>Extra configuration a test class can layer on. Applied last.</summary>
    protected virtual IDictionary<string, string?> Configuration => new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);

        builder.UseEnvironment(Environments.Development);

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(_root, "app.db")}",
            ["Storage:RootPath"] = Path.Combine(_root, "files"),
            ["Email:Sink"] = Path.Combine(_root, "mail"),

            // A test-only key. Not a secret, and not the shape of one used anywhere real.
            ["Jwt:SigningKey"] = "test-signing-key-at-least-32-characters-long",
            ["Jwt:Issuer"] = "fileinvite-lite-tests",
            ["Jwt:Audience"] = "fileinvite-lite-tests",

            ["Seed:DemoStaffEmail"] = DemoEmail,
            ["Seed:DemoStaffPassword"] = DemoPassword,

            // Raised so that a class not testing the limiter cannot trip it. The class that does
            // test it sets the production value back.
            ["RateLimits:LoginPermitLimit"] = "1000",
        };

        foreach (var (key, value) in Configuration)
        {
            settings[key] = value;
        }

        builder.UseSetting("ConfigureTest", "true");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }

    /// <summary>Runs work against the database directly, outside an HTTP request.</summary>
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind on Windows because SQLite still holds the file is not
            // worth failing a test run over.
        }
    }
}
