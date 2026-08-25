using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services;

public class DiffFileCollectorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cpmigrate-diffs").FullName;

    public void Dispose()
    {
        Directory.Delete(_tempDir, true);
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public void Begin_CreatesEmptyFile_AtStartOfRun()
    {
        var path = TempPath("diffs.patch");

        new DiffFileCollector().Begin(path);

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().BeEmpty();
    }

    [Fact]
    public void Begin_TruncatesExistingFile_SoRunsNeverInheritStaleDiffs()
    {
        var path = TempPath("diffs.patch");
        File.WriteAllText(path, "stale content from a previous run");

        new DiffFileCollector().Begin(path);

        File.ReadAllText(path).Should().BeEmpty();
    }

    [Fact]
    public void Append_MultipleDiffs_AppendsInOrder_SeparatedByBlankLine()
    {
        var path = TempPath("diffs.patch");
        var collector = new DiffFileCollector();
        collector.Begin(path);
        var first = UnifiedDiffGenerator.Generate("old one\n", "new one\n", "First.props");
        var second = UnifiedDiffGenerator.Generate("old two\n", "new two\n", "Second.props");

        collector.Append(first);
        collector.Append(second);

        var content = File.ReadAllText(path);
        content.Should().Be(first + "\n" + second);
        content.IndexOf("First.props").Should().BeLessThan(content.IndexOf("Second.props"));
    }

    [Fact]
    public void Append_UsesTheGeneratorHeaders_SoTheArtifactIsAValidUnifiedDiff()
    {
        var path = TempPath("diffs.patch");
        var collector = new DiffFileCollector();
        collector.Begin(path);

        collector.Append(UnifiedDiffGenerator.Generate("before\n", "after\n", "Directory.Packages.props"));

        var content = File.ReadAllText(path);
        content.Should().Contain("--- a/Directory.Packages.props");
        content.Should().Contain("+++ b/Directory.Packages.props");
    }

    [Fact]
    public void Append_BeforeBegin_IsANoOp_NotAnError()
    {
        var path = TempPath("never-created.patch");

        var append = () => new DiffFileCollector().Append("some diff");

        append.Should().NotThrow();
        File.Exists(path).Should().BeFalse();
    }
}

public class DiffFileOptionValidationTests
{
    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Sarif)]
    public void Validate_DiffFileWithMachineReadableOutput_ThrowsArgumentException(OutputFormat output)
    {
        var options = new Options { Output = output, DiffFile = "diffs.patch" };

        var action = () => options.Validate();

        action.Should().Throw<ArgumentException>()
            .WithMessage("*--diff-file cannot be used with --output Json or --output Sarif*");
    }

    [Fact]
    public void Validate_DiffFileWithTerminalOutput_DoesNotThrow()
    {
        var options = new Options { Output = OutputFormat.Terminal, DiffFile = "diffs.patch" };

        var action = () => options.Validate();

        action.Should().NotThrow();
    }
}

public class DiffFileMigrationIntegrationTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cpmigrate-diffrun").FullName;
    private readonly Mock<IConsoleService> _mockConsole = new();
    private readonly Mock<IProjectAnalyzer> _mockAnalyzer = new();
    private readonly Mock<IAnalysisService> _mockAnalysis = new();
    private readonly Mock<IFixService> _mockFix = new();

    private MigrationService CreateService() =>
        new(
            _mockConsole.Object,
            _mockAnalyzer.Object,
            new VersionResolver(_mockConsole.Object),
            null,
            new BackupManager(),
            _mockAnalysis.Object,
            _mockFix.Object
        );

    private string SetupSingleProjectRun(Options options)
    {
        var projectPath = Path.Combine(_tempDir, "P1.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """
        );
        options.SolutionFileDir = _tempDir;

        _mockAnalyzer
            .Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((_tempDir, new List<string> { projectPath }));
        _mockAnalyzer
            .Setup(a => a.ScanResolvedPackagesAsync(projectPath, It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync((
                new List<PackageReference>
                {
                    new("Newtonsoft.Json", "13.0.1", projectPath, "P1.csproj"),
                },
                true));
        _mockAnalyzer
            .Setup(a => a.ScanProjectPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), true));
        _mockAnalysis
            .Setup(a => a.Analyze(It.IsAny<ProjectPackageInfo>()))
            .Returns(new AnalysisReport(1, 0, new List<AnalyzerResult>()));

        return projectPath;
    }

    [Fact]
    public async Task DryRunWithDiffFile_WritesOneUnifiedDiffPerChange_EvenWithoutRenderDiffFlag()
    {
        var diffPath = Path.Combine(_tempDir, "diffs.patch");
        SetupSingleProjectRun(new Options());
        var service = CreateService();

        var result = await service.ExecuteAsync(
            new Options { DryRun = true, DiffFile = diffPath, SolutionFileDir = _tempDir }
        );

        result.ExitCode.Should().Be(ExitCodes.Success);
        var content = File.ReadAllText(diffPath);
        content.Should().Contain("--- a/Directory.Packages.props");
        content.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        // The console preview is untouched by collection: no diff was rendered because --diff is off.
        _mockConsole.Verify(c => c.WriteDiff(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DryRunWithNoProjects_StillLeavesAnEmptyDiffFile()
    {
        var diffPath = Path.Combine(_tempDir, "empty.patch");
        var options = new Options { DryRun = true, DiffFile = diffPath, SolutionFileDir = _tempDir };
        _mockAnalyzer
            .Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((_tempDir, new List<string>()));

        var result = await CreateService().ExecuteAsync(options);

        result.ExitCode.Should().Be(ExitCodes.NoProjectsFound);
        File.Exists(diffPath).Should().BeTrue("a missing artifact means the run crashed, not that nothing changed");
        File.ReadAllText(diffPath).Should().BeEmpty();
    }

    public void Dispose() => Directory.Delete(_tempDir, true);
}
