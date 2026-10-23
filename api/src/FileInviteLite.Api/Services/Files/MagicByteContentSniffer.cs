using System.IO.Compression;

namespace FileInviteLite.Api.Services.Files;

public sealed class MagicByteContentSniffer : IContentSniffer
{
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.Ordinal)
    {
        [".pdf"] = "application/pdf",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".docx"] = Docx,
        [".xlsx"] = Xlsx,
    };

    public string? ContentTypeFor(string extension) =>
        ContentTypes.GetValueOrDefault(extension.ToLowerInvariant());

    public async Task<bool> MatchesAsync(string extension, Stream content, CancellationToken ct)
    {
        var header = new byte[8];
        var read = await ReadUpToAsync(content, header, ct);
        var head = header.AsSpan(0, read);

        switch (extension.ToLowerInvariant())
        {
            case ".pdf":
                return head.StartsWith(Pdf);
            case ".png":
                return head.StartsWith(Png);
            case ".jpg":
            case ".jpeg":
                return head.StartsWith(Jpeg);
            case ".docx":
                return head.StartsWith(Zip) && HasEntries(content, "[Content_Types].xml", "word/document.xml");
            case ".xlsx":
                return head.StartsWith(Zip) && HasEntries(content, "[Content_Types].xml", "xl/workbook.xml");
            default:
                return false;
        }
    }

    /// <summary>
    /// Office files are ZIP containers, so the ZIP header alone proves nothing: any archive starts
    /// with it. The entry that makes the file a Word or Excel document must be there too. Entry
    /// names are read from the central directory; nothing is decompressed.
    /// </summary>
    private static bool HasEntries(Stream content, params string[] names)
    {
        try
        {
            content.Seek(0, SeekOrigin.Begin);
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var present = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            return names.All(present.Contains);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
