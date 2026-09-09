using System.Text.Json;
using CPMigrate.Services;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services;

/// <summary>
/// `--init` scaffolds the team config: create it with sane defaults, refuse to clobber an
/// existing one without `--force`, and overwrite when asked. The refusal path is the one that
/// protects a hand-tuned config, so it gets its own pins.
/// </summary>
public class InitServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly Mock<IConsoleService> _console;

    public InitServiceTests()
    {
        _dir = Directory.CreateTempSubdirectory("cpmigrate-init").FullName;
        _console = new Mock<IConsoleService>();
        _console.SetupGet(c => c.IsInteractive).Returns(false);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort cleanup of the sandbox directory.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_FreshDirectory_CreatesDefaultConfig()
    {
        var exit = await CreateService().RunAsync(_dir, force: false);

        exit.Should().Be(ExitCodes.Success);
        var path = Path.Combine(_dir, ".cpmigrate.json");
        File.Exists(path).Should().BeTrue();
        var payload = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        payload.GetProperty("conflictStrategy").GetString().Should().Be("highest");
        payload.GetProperty("backup").GetBoolean().Should().BeTrue();
        payload.GetProperty("failOn").GetString().Should().Be("high");
        _console.Verify(c => c.Success(It.Is<string>(s => s.Contains("Created"))), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ExistingConfigWithoutForce_RefusesAndKeepsTheFile()
    {
        var path = Path.Combine(_dir, ".cpmigrate.json");
        File.WriteAllText(path, "{ \"hand\": \"tuned\" }");

        var exit = await CreateService().RunAsync(_dir, force: false);

        exit.Should().Be(ExitCodes.FileOperationError);
        File.ReadAllText(path).Should().Be("{ \"hand\": \"tuned\" }");
        _console.Verify(c => c.Warning(It.Is<string>(s => s.Contains("already exists"))), Times.Once);
        _console.Verify(c => c.Info(It.Is<string>(s => s.Contains("--force"))), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ExistingConfigWithForce_Overwrites()
    {
        var path = Path.Combine(_dir, ".cpmigrate.json");
        File.WriteAllText(path, "{ \"hand\": \"tuned\" }");

        var exit = await CreateService().RunAsync(_dir, force: true);

        exit.Should().Be(ExitCodes.Success);
        File.ReadAllText(path).Should().NotBe("{ \"hand\": \"tuned\" }");
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty("backup", out _).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_FilePath_ResolvesToItsParentDirectory()
    {
        var file = Path.Combine(_dir, "App.sln");
        File.WriteAllText(file, string.Empty);

        var exit = await CreateService().RunAsync(file, force: false);

        exit.Should().Be(ExitCodes.Success);
        File.Exists(Path.Combine(_dir, ".cpmigrate.json")).Should().BeTrue();
    }

    private InitService CreateService() => new(_console.Object);
}
