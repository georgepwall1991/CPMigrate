using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class FileHelperTests : IDisposable
{
    private readonly string _testDirectory;

    public FileHelperTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateFileHelperTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAtomicAsync_WritesContentCorrectly()
    {
        var filePath = Path.Combine(_testDirectory, "test.txt");
        var content = "Hello, World!";

        await FileHelper.WriteAtomicAsync(filePath, content);

        File.Exists(filePath).Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be(content);
    }

    [Fact]
    public async Task WriteAtomicAsync_OverwritesExistingFile()
    {
        var filePath = Path.Combine(_testDirectory, "test.txt");
        await File.WriteAllTextAsync(filePath, "old content");

        await FileHelper.WriteAtomicAsync(filePath, "new content");

        (await File.ReadAllTextAsync(filePath)).Should().Be("new content");
    }

    [Fact]
    public async Task WriteAtomicAsync_NoTempFileLeftBehind()
    {
        var filePath = Path.Combine(_testDirectory, "test.txt");

        await FileHelper.WriteAtomicAsync(filePath, "content");

        // Only the target file should exist, no temp files
        var files = Directory.GetFiles(_testDirectory);
        files.Should().HaveCount(1);
        files[0].Should().EndWith("test.txt");
    }

    [Fact]
    public void WriteAtomic_WritesContentCorrectly()
    {
        var filePath = Path.Combine(_testDirectory, "test.txt");

        FileHelper.WriteAtomic(filePath, "Hello, World!");

        File.ReadAllText(filePath).Should().Be("Hello, World!");
    }

    [Fact]
    public void WriteAtomic_OverwritesExistingFileAndLeavesNoTemp()
    {
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "old content");

        FileHelper.WriteAtomic(filePath, "new content");

        File.ReadAllText(filePath).Should().Be("new content");
        Directory.GetFiles(_testDirectory).Should().HaveCount(1);
    }

    [Fact]
    public void WriteAtomic_CreatesMissingParentDirectories()
    {
        var filePath = Path.Combine(_testDirectory, "sub", "dir", "test.txt");

        FileHelper.WriteAtomic(filePath, "content");

        File.ReadAllText(filePath).Should().Be("content");
    }

    [Fact]
    public void WriteAtomic_WhenTargetPathIsImpossible_ThrowsAndLeavesNoTemp()
    {
        // The guarantee the temp-file dance exists for: a failed write never leaves a partial
        // target or a stray temp file behind.
        var blockingFile = Path.Combine(_testDirectory, "blocker");
        File.WriteAllText(blockingFile, "untouched");

        var act = () => FileHelper.WriteAtomic(Path.Combine(blockingFile, "nested.txt"), "x");

        act.Should().Throw<Exception>();
        File.ReadAllText(blockingFile).Should().Be("untouched");
        Directory.GetFiles(_testDirectory).Should().HaveCount(1);
    }

    [Fact]
    public void WriteAtomic_ReadOnlyTarget_RefusesLikeAPlainWrite()
    {
        // rename() only needs directory write permission, so without an explicit check the move
        // would silently replace a read-only file that File.WriteAllText refuses to open. A file
        // marked read-only was marked for a reason.
        var filePath = Path.Combine(_testDirectory, "locked.txt");
        File.WriteAllText(filePath, "original");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);
        try
        {
            var act = () => FileHelper.WriteAtomic(filePath, "changed");

            act.Should().Throw<UnauthorizedAccessException>();
            File.ReadAllText(filePath).Should().Be("original");
            Directory.GetFiles(_testDirectory).Should().HaveCount(1);
        }
        finally
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
    }
}
