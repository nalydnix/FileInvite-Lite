using System.Text;

namespace FileInviteLite.Api.Services.Email;

/// <summary>
/// Registered only in Development. Writes each message to a <c>.txt</c> file in <c>Email:Sink</c>
/// so a developer can open the link without a mail server (S1.15). The file holds the link, which
/// is a credential, so the sink is a local, gitignored folder; the file name is random and carries
/// neither the token nor the recipient.
/// </summary>
public sealed class DevSinkEmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task SendMagicLinkAsync(MagicLinkEmail message, CancellationToken ct)
    {
        var sink = configuration["Email:Sink"];
        if (string.IsNullOrWhiteSpace(sink))
        {
            throw new InvalidOperationException(
                "Email:Sink is not configured, so the dev email sink has nowhere to write.");
        }

        Directory.CreateDirectory(sink);
        var path = Path.Combine(sink, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.txt");

        var body = new StringBuilder()
            .AppendLine($"To: {message.ToAddress}")
            .AppendLine("Subject: Documents requested")
            .AppendLine()
            .AppendLine($"Hello {message.ClientName},")
            .AppendLine()
            .AppendLine("Please upload the documents we asked for using this private link:")
            .AppendLine()
            .AppendLine(message.Url)
            .AppendLine()
            .AppendLine($"The link expires on {message.ExpiresAt:yyyy-MM-dd HH:mm} UTC.")
            .ToString();

        await File.WriteAllTextAsync(path, body, ct);
    }
}
