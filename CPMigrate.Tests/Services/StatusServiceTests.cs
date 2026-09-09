using CPMigrate.Services;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services;

/// <summary>
/// `--status` is the first thing a newcomer runs: it must exit 0 on any readable workspace and
/// narrate what it found — CPM state, team config, project counts — rather than going quiet.
/// These pins lock the exit code and each narration branch.
/// </summary>
public class StatusServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly Mock<IConsoleService> _console;
    private readonly Mock<ISolutionDiscovery> _discovery;

    public StatusServiceTests()
    {
        _dir = Directory.CreateTempSubdirectory("cpmigrate-status").FullName;
        _console = new Mock<IConsoleService>();
        _discovery = new Mock<ISolutionDiscovery>();
        _discovery.Setup(d => d.GetSolutionFiles(It.IsAny<string>())).Returns(Array.Empty<string>());
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
    public async Task RunAsync_EmptyDirectory_ExitsSuccessAndReportsMissingPieces()
    {
        var exit = await CreateService().RunAsync(_dir);

        exit.Should().Be(ExitCodes.Success);
        _console.Verify(c => c.Banner(It.Is<string>(s => s.Contains("WORKSPACE STATUS"))), Times.Once);
        _console.Verify(c => c.Dim(It.Is<string>(s => s.Contains("No Directory.Packages.props"))), Times.Once);
        _console.Verify(c => c.Dim(It.Is<string>(s => s.Contains("No .cpmigrate.json"))), Times.Once);
    }

    [Fact]
    public async Task RunAsync_CentrallyManagedPackages_ReportsTheCount()
    {
        File.WriteAllText(
            Path.Combine(_dir, "Directory.Packages.props"),
            "<Project><ItemGroup><PackageVersion Include=\"A\" Version=\"1.0\" />"
            + "<PackageVersion Include=\"B\" Version=\"2.0\" /></ItemGroup></Project>");

        var exit = await CreateService().RunAsync(_dir);

        exit.Should().Be(ExitCodes.Success);
        _console.Verify(
            c => c.Success(It.Is<string>(s => s.Contains("CPM active: 2 package version(s)"))),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_TeamConfig_ConfirmsWhenPresent()
    {
        File.WriteAllText(Path.Combine(_dir, ".cpmigrate.json"), "{}");

        await CreateService().RunAsync(_dir);

        _console.Verify(c => c.Dim(It.Is<string>(s => s.Contains("Team config"))), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ProjectsAndSolutions_AreCounted()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "App.csproj"), "<Project />");
        _discovery.Setup(d => d.GetSolutionFiles(_dir)).Returns(new[] { "App.sln" });

        await CreateService().RunAsync(_dir);

        _console.Verify(
            c => c.Dim(It.Is<string>(s => s.Contains("1 project(s) across 1 solution(s)"))),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_FilePath_ResolvesToItsParentDirectory()
    {
        var file = Path.Combine(_dir, "App.sln");
        File.WriteAllText(file, string.Empty);

        await CreateService().RunAsync(file);

        _discovery.Verify(d => d.GetSolutionFiles(_dir), Times.Once);
    }

    private StatusService CreateService() => new(_console.Object, _discovery.Object);
}
