namespace FileInviteLite.Api.Services.Email;

/// <summary>
/// Delivers the magic link to the client. The MVP has no real mail transport: Development writes
/// the message to a folder (<see cref="DevSinkEmailSender"/>) and every other environment does
/// nothing (<see cref="NoOpEmailSender"/>). Which one runs is decided by registration in
/// Program.cs, never by a branch inside a sender.
/// </summary>
public interface IEmailSender
{
    Task SendMagicLinkAsync(MagicLinkEmail message, CancellationToken ct);
}

/// <summary>The link is a credential. The record's text form hides it, so it cannot reach a log by accident.</summary>
public sealed record MagicLinkEmail(string ToAddress, string ClientName, string Url, DateTime ExpiresAt)
{
    public override string ToString() => $"{nameof(MagicLinkEmail)} {{ Url = *** }}";
}
