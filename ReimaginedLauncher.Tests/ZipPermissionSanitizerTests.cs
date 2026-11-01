using System.IO.Compression;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ZipPermissionSanitizerTests : IDisposable
{
    private const int UnixRegularFile = 0x8000;
    private const int UnixReadOnly = 0x124;
    private const int UnixWriteOnly = 0x080;
    private const int DosArchiveAttribute = 0x20;
    private const uint UnixPermissionMask = 0x0FFFu << 16;

    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-launcher-zip-sanitizer-tests-{Guid.NewGuid():N}");

    [Fact]
    public void StripsUnixBitsAndKeepsDosAttributesAndContent()
    {
        if (OperatingSystem.IsWindows()) return;
        var archivePath = CreateArchive(
            dataDescriptors: false,
            comment: null,
            ("mods/Reimagined/readonly.bin", "readonly", UnixMode(UnixReadOnly) | DosArchiveAttribute),
            ("mods/Reimagined/writeonly.bin", "writeonly", UnixMode(UnixWriteOnly)),
            ("mods/Reimagined/plain.bin", "plain", DosArchiveAttribute));

        ZipPermissionSanitizer.StripUnixPermissions(archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.All(archive.Entries, entry => Assert.Equal(0u, (uint)entry.ExternalAttributes & UnixPermissionMask));
        Assert.Equal(UnixMode(0) | DosArchiveAttribute, archive.GetEntry("mods/Reimagined/readonly.bin")!.ExternalAttributes);
        Assert.Equal(DosArchiveAttribute, archive.GetEntry("mods/Reimagined/plain.bin")!.ExternalAttributes);
        Assert.Equal("readonly", ReadEntry(archive, "mods/Reimagined/readonly.bin"));
        Assert.Equal("writeonly", ReadEntry(archive, "mods/Reimagined/writeonly.bin"));
        Assert.Equal("plain", ReadEntry(archive, "mods/Reimagined/plain.bin"));
    }

    [Fact]
    public void ExtractedFilesAreReadableAndWritableAfterSanitizing()
    {
        if (OperatingSystem.IsWindows()) return;
        var archivePath = CreateArchive(
            dataDescriptors: false,
            comment: null,
            ("data/readonly.bin", "readonly", UnixMode(UnixReadOnly)),
            ("data/writeonly.bin", "writeonly", UnixMode(UnixWriteOnly)));
        var destination = Path.Combine(_testDirectory, "extracted");

        ZipPermissionSanitizer.StripUnixPermissions(archivePath);
        ZipFile.ExtractToDirectory(archivePath, destination);

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        foreach (var file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(expected, File.GetUnixFileMode(file) & expected);
        }

        ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
    }

    [Fact]
    public void KeepsDataDescriptorArchivesValid()
    {
        if (OperatingSystem.IsWindows()) return;
        var archivePath = CreateArchive(
            dataDescriptors: true,
            comment: null,
            ("mods/a.bin", new string('a', 50_000), UnixMode(UnixReadOnly)),
            ("mods/b.bin", new string('b', 50_000), UnixMode(UnixReadOnly)));

        ZipPermissionSanitizer.StripUnixPermissions(archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.All(archive.Entries, entry => Assert.Equal(0u, (uint)entry.ExternalAttributes & UnixPermissionMask));
        Assert.Equal(new string('a', 50_000), ReadEntry(archive, "mods/a.bin"));
        Assert.Equal(new string('b', 50_000), ReadEntry(archive, "mods/b.bin"));
    }

    [Fact]
    public void FindsTheCentralDirectoryBehindAnArchiveComment()
    {
        if (OperatingSystem.IsWindows()) return;
        var archivePath = CreateArchive(
            dataDescriptors: false,
            comment: "Created on Windows. " + new string('x', 4000),
            ("mods/a.bin", "a", UnixMode(UnixReadOnly)));

        ZipPermissionSanitizer.StripUnixPermissions(archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(0u, (uint)archive.GetEntry("mods/a.bin")!.ExternalAttributes & UnixPermissionMask);
        Assert.Equal("a", ReadEntry(archive, "mods/a.bin"));
    }

    [Fact]
    public void LeavesArchivesWithoutUnixBitsUntouched()
    {
        var archivePath = CreateArchive(
            dataDescriptors: false,
            comment: null,
            ("mods/a.bin", "a", DosArchiveAttribute),
            ("mods/b.bin", "b", 0));
        var original = File.ReadAllBytes(archivePath);
        var lastWrite = File.GetLastWriteTimeUtc(archivePath);

        ZipPermissionSanitizer.StripUnixPermissions(archivePath);

        Assert.Equal(original, File.ReadAllBytes(archivePath));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(archivePath));
    }

    [Fact]
    public void LeavesFilesThatAreNotArchivesUntouched()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "not-a-zip.zip");
        File.WriteAllText(path, "PK this is not really an archive");
        var original = File.ReadAllBytes(path);

        ZipPermissionSanitizer.StripUnixPermissions(path);
        ZipPermissionSanitizer.StripUnixPermissions(Path.Combine(_testDirectory, "missing.zip"));

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void LeavesTruncatedArchivesUntouched()
    {
        var archivePath = CreateArchive(
            dataDescriptors: false,
            comment: null,
            ("mods/a.bin", new string('a', 10_000), UnixMode(UnixReadOnly)));
        var original = File.ReadAllBytes(archivePath);
        var truncated = original[..(original.Length / 2)];
        File.WriteAllBytes(archivePath, truncated);

        ZipPermissionSanitizer.StripUnixPermissions(archivePath);

        Assert.Equal(truncated, File.ReadAllBytes(archivePath));
    }

    private static int UnixMode(int permissions) => unchecked((int)((uint)(UnixRegularFile | permissions) << 16));

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private string CreateArchive(
        bool dataDescriptors,
        string? comment,
        params (string Path, string Content, int Attributes)[] entries)
    {
        Directory.CreateDirectory(_testDirectory);
        var archivePath = Path.Combine(_testDirectory, $"{Guid.NewGuid():N}.zip");
        using var fileStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write);
        // ZipArchive writes data descriptors, as streaming Windows tools do, only when it cannot seek.
        using Stream output = dataDescriptors ? new WriteOnlyStream(fileStream) : fileStream;
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        if (comment is not null)
        {
            archive.Comment = comment;
        }

        foreach (var (path, content, attributes) in entries)
        {
            var entry = archive.CreateEntry(path);
            entry.ExternalAttributes = attributes;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return archivePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
