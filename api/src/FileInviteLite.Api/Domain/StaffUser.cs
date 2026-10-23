namespace FileInviteLite.Api.Domain;

public sealed class StaffUser
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string Name { get; set; }

    /// <summary>ASP.NET Core <c>PasswordHasher</c> output. Never a plaintext password (S8.3).</summary>
    public required string PasswordHash { get; set; }
}
