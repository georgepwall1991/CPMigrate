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
}
