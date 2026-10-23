using System.Globalization;
using System.Text.RegularExpressions;

namespace FileInviteLite.Api.Services.Storage;

/// <summary>
/// Writes files under <c>Storage:RootPath</c> at <c>yyyy/MM/{guid}</c>: no extension, no part of the
/// original name (ADR 0005). The root is never mapped as a static path.
/// </summary>
public sealed partial class LocalDiskFileStorage(IConfiguration configuration, TimeProvider clock) : IFileStorage
{
    private const int BufferSize = 81920;

    [GeneratedRegex(@"^\d{4}/\d{2}/[0-9a-f]{32}$")]
    private static partial Regex KeyShape();

    private string Root
    {
        get
        {
            var configured = configuration["Storage:RootPath"];
            return string.IsNullOrWhiteSpace(configured)
                ? throw new InvalidOperationException("Storage:RootPath is not configured.")
                : Path.GetFullPath(configured);
        }
    }

    public async Task<string> SaveAsync(Stream content, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var key = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}");
        var finalPath = PathFor(key);

        // Written beside the final location under a temporary name and moved into place only when
        // complete, so a reader never sees a partial file and a failure leaves nothing under a key.
        var incoming = Path.Combine(Root, ".incoming");
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var tempPath = Path.Combine(incoming, $"{Guid.NewGuid():N}.part");

        try
        {
            await using (var target = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(target, BufferSize, ct);
            }

            File.Move(tempPath, finalPath);   // no overwrite: a key is never reused
            return key;
        }
        catch
        {
            DeleteQuietly(tempPath);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(
            PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan));

    public Task<bool> ExistsAsync(string key, CancellationToken ct) =>
        Task.FromResult(File.Exists(PathFor(key)));

    public Task DiscardAsync(string key, CancellationToken ct)
    {
        DeleteQuietly(PathFor(key));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a key to a path under the root, refusing anything that is not exactly the shape this
    /// class generates, so a key can never carry a path component out of the root.
    /// </summary>
    private string PathFor(string key)
    {
        if (!KeyShape().IsMatch(key))
        {
            throw new ArgumentException("Not a valid storage key.", nameof(key));
        }

        return Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temporary file is housekeeping, not a reason to mask the
            // failure that got us here.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
