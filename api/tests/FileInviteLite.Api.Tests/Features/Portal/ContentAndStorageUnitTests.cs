using FileInviteLite.Api.Services.Files;
using FileInviteLite.Api.Services.Storage;
using Microsoft.Extensions.Configuration;
using static FileInviteLite.Api.Tests.Features.Portal.PortalTestKit;

namespace FileInviteLite.Api.Tests.Features.Portal;

public sealed class MagicByteContentSnifferTests
{
    private readonly MagicByteContentSniffer _sniffer = new();

    private Task<bool> MatchesAsync(string extension, byte[] content) =>
        _sniffer.MatchesAsync(extension, new MemoryStream(content), CancellationToken.None);

    [Theory]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".PDF", "application/pdf")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public void ContentTypeFor_NamesTheAllowedExtensions_S3_5(string extension, string expected) =>
        Assert.Equal(expected, _sniffer.ContentTypeFor(extension));

    [Theory]
    [InlineData(".exe")]
    [InlineData(".zip")]
    [InlineData(".html")]
    [InlineData(".svg")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("pdf")]
    public void ContentTypeFor_RefusesEverythingElse_S3_5(string extension) =>
        Assert.Null(_sniffer.ContentTypeFor(extension));

    [Fact]
    public async Task Matches_RealContentOfEachType_S3_6()
    {
        Assert.True(await MatchesAsync(".pdf", Pdf()));
        Assert.True(await MatchesAsync(".png", Png()));
        Assert.True(await MatchesAsync(".jpg", Jpeg()));
        Assert.True(await MatchesAsync(".jpeg", Jpeg()));
        Assert.True(await MatchesAsync(".docx", Docx()));
        Assert.True(await MatchesAsync(".xlsx", Xlsx()));
    }

    [Fact]
    public async Task DoesNotMatch_AnExecutableUnderAnyAllowedExtension_S3_6()
    {
        foreach (var extension in new[] { ".pdf", ".png", ".jpg", ".jpeg", ".docx", ".xlsx" })
        {
            Assert.False(await MatchesAsync(extension, Exe()), extension);
        }
    }

    [Fact]
    public async Task DoesNotMatch_AZipThatIsNotAnOfficeFile_S3_6()
    {
        Assert.False(await MatchesAsync(".docx", Zip("readme.txt")));
        Assert.False(await MatchesAsync(".xlsx", Zip("readme.txt")));
        Assert.False(await MatchesAsync(".docx", Zip("word/document.xml")));          // no [Content_Types].xml
        Assert.False(await MatchesAsync(".docx", Xlsx()));
        Assert.False(await MatchesAsync(".xlsx", Docx()));
    }

    [Fact]
    public async Task DoesNotMatch_ATruncatedOrEmptyFile_S3_6()
    {
        Assert.False(await MatchesAsync(".pdf", []));
        Assert.False(await MatchesAsync(".png", Png()[..4]));
        Assert.False(await MatchesAsync(".docx", Docx()[..30]));   // a PK header with no central directory
        Assert.False(await MatchesAsync(".docx", [0x50, 0x4B, 0x03, 0x04]));
    }
}

public sealed class LocalDiskFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fileinvite-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalDiskFileStorage _storage;

    public LocalDiskFileStorageTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:RootPath"] = _root })
            .Build();
        _storage = new LocalDiskFileStorage(config, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string[] FilesOnDisk() =>
        Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task Save_WritesTheContentUnderAnOpaqueYearMonthGuidKey_S3_8()
    {
        var key = await _storage.SaveAsync(new MemoryStream([1, 2, 3]), CancellationToken.None);

        Assert.Matches(@"^\d{4}/\d{2}/[0-9a-f]{32}$", key);
        Assert.True(await _storage.ExistsAsync(key, CancellationToken.None));
        await using var read = await _storage.OpenReadAsync(key, CancellationToken.None);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        Assert.Single(FilesOnDisk());
    }

    [Fact]
    public async Task Save_WhenTheSourceStreamFails_LeavesNoFileBehind()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.SaveAsync(new FailingStream(), CancellationToken.None));

        Assert.Empty(FilesOnDisk());
    }

    [Fact]
    public async Task Discard_RemovesTheBlob_AndIsHarmlessTwice()
    {
        var key = await _storage.SaveAsync(new MemoryStream([1]), CancellationToken.None);

        await _storage.DiscardAsync(key, CancellationToken.None);
        await _storage.DiscardAsync(key, CancellationToken.None);

        Assert.False(await _storage.ExistsAsync(key, CancellationToken.None));
        Assert.Empty(FilesOnDisk());
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("2026/10/../../../evil")]
    [InlineData("2026/10/00000000000000000000000000000000/../x")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("2026/10/ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("")]
    public async Task EveryOperation_RefusesAKeyThatIsNotTheGeneratedShape_S3_8(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.OpenReadAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.ExistsAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DiscardAsync(key, CancellationToken.None));
    }

    private sealed class FailingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_reads++ == 0)
            {
                buffer.Span[0] = 7;
                return 1;
            }

            throw new InvalidOperationException("the source failed partway");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
