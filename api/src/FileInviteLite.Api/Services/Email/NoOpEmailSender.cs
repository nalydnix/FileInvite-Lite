namespace FileInviteLite.Api.Services.Email;

/// <summary>Registered everywhere except Development. Sends nothing and records nothing.</summary>
public sealed class NoOpEmailSender : IEmailSender
{
    public Task SendMagicLinkAsync(MagicLinkEmail message, CancellationToken ct) => Task.CompletedTask;
}
