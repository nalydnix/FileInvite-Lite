using FileInviteLite.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Data.Seed;

/// <summary>
/// Creates the single demo staff user, if it does not already exist.
/// </summary>
/// <remarks>
/// The password comes from configuration and has no default. There is deliberately no password in
/// this file, in appsettings, or in the logs: seeding a known-by-everyone credential into a
/// publicly reachable app is the kind of shortcut that makes the rest of the auth work pointless.
/// Set it locally with:
/// <code>dotnet user-secrets set "Seed:DemoStaffPassword" "&lt;something long&gt;" --project api/src/FileInviteLite.Api</code>
/// With no password configured, seeding is skipped and the app starts with no staff user.
/// </remarks>
public static class DemoDataSeeder
{
    public static async Task SeedAsync(
        AppDbContext db, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        var email = config["Seed:DemoStaffEmail"] ?? "demo@fileinvite-lite.local";
        var password = config["Seed:DemoStaffPassword"];

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No demo staff user was seeded: Seed:DemoStaffPassword is not configured. " +
                "Set it with: dotnet user-secrets set \"Seed:DemoStaffPassword\" \"<password>\" " +
                "--project api/src/FileInviteLite.Api");
            return;
        }

        if (await db.StaffUsers.AnyAsync(u => u.Email == email, ct))
        {
            return;
        }

        var user = new StaffUser
        {
            Email = email,
            Name = config["Seed:DemoStaffName"] ?? "Demo Staff",
            PasswordHash = string.Empty,
        };
        user.PasswordHash = new PasswordHasher<StaffUser>().HashPassword(user, password);

        db.StaffUsers.Add(user);
        await db.SaveChangesAsync(ct);

        // The email is not a secret; the password is, and is not logged.
        logger.LogInformation("Seeded demo staff user {Email}.", email);
    }
}
