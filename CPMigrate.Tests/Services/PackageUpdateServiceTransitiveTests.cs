using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services;

public class PackageUpdateServiceTransitiveTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _consoleService;
    private readonly Mock<IProjectAnalyzer> _projectAnalyzerMock;
    private readonly FakeUpdateCandidateSource _candidates = new();
    private readonly Mock<IDotNetCliService> _dotNetCliMock;
    private readonly Mock<IBackupManager> _backupManagerMock;
    private readonly PropsGenerator _propsGenerator;
    private readonly PackageUpdateService _sut;

    public PackageUpdateServiceTransitiveTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateTransTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _consoleService = new FakeConsoleService();
        _projectAnalyzerMock = new Mock<IProjectAnalyzer>();
        _dotNetCliMock = new Mock<IDotNetCliService>();
        _backupManagerMock = new Mock<IBackupManager>();
        _propsGenerator = new PropsGenerator();

        _sut = new PackageUpdateService(
            _consoleService,
            _projectAnalyzerMock.Object,
            _propsGenerator,
            _candidates,
            _dotNetCliMock.Object,
            _backupManagerMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TransitiveUpdatesDiscovered_AppearInUpdateList()
    {
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        AddTransitiveUpdate("System.Text.Encodings.Web", "7.0.0", "8.0.0");
        _candidates.TransitivePackagesFound = 1;

        _consoleService.SelectionResponses.Enqueue("Accept major update to 8.0.0");

        SetupBackupAndBuild();

        var result = await _sut.UpdatePackagesAsync(CreateOptions(includeTransitive: true));

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesFound.Should().BeGreaterThan(0);
        result.TransitivePackagesUpdated.Should().Be(1);
        result.Updates.Should().Contain(u =>
            u.PackageName == "System.Text.Encodings.Web" && u.IsTransitive && u.Accepted);
    }

    [Fact]
    public async Task TransitiveDepsExcludedIfAlreadyDirect()
    {
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        // A restore-backed source never emits a transitive duplicate of a central pin.
        _candidates.TransitivePackagesFound = 1;

        SetupBackupAndBuild();

        var result = await _sut.UpdatePackagesAsync(CreateOptions(includeTransitive: true));

        result.Updates.Where(u => u.PackageName == "Newtonsoft.Json").Should().HaveCount(1);
        result.Updates.Should().NotContain(u => u.PackageName == "Newtonsoft.Json" && u.IsTransitive);
    }

    [Fact]
    public async Task TransitiveDryRun_ShowsBothSections()
    {
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        AddTransitiveUpdate("System.Text.Encodings.Web", "7.0.0", "7.0.1");
        _candidates.TransitivePackagesFound = 1;

        var result = await _sut.UpdatePackagesAsync(CreateOptions(dryRun: true, includeTransitive: true));

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PackagesUpdated.Should().Be(1);
        result.TransitivePackagesUpdated.Should().Be(1);
        result.Updates.Should().Contain(u => u.PackageName == "Newtonsoft.Json" && !u.IsTransitive);
        result.Updates.Should().Contain(u => u.PackageName == "System.Text.Encodings.Web" && u.IsTransitive);
    }

    [Fact]
    public async Task TransitiveUpdatesPinnedInProps()
    {
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        AddTransitiveUpdate("System.Text.Encodings.Web", "7.0.0", "7.0.1");
        _candidates.TransitivePackagesFound = 1;

        SetupBackupAndBuild();

        var result = await _sut.UpdatePackagesAsync(CreateOptions(includeTransitive: true));

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesUpdated.Should().Be(1);

        var propsContent = await File.ReadAllTextAsync(propsPath);
        propsContent.Should().Contain("System.Text.Encodings.Web");
        propsContent.Should().Contain("7.0.1");
    }

    [Fact]
    public async Task TransitiveScanFailure_SkipsGracefully()
    {
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        _candidates.TransitiveScanFailed = true;
        _candidates.TransitivePackagesFound = 0;

        SetupBackupAndBuild();

        var result = await _sut.UpdatePackagesAsync(CreateOptions(includeTransitive: true));

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesFound.Should().Be(0);
        result.TransitivePackagesUpdated.Should().Be(0);
        _consoleService.OutputMessages.Should().Contain(m => m.Contains("Could not scan transitive"));
    }

    [Fact]
    public async Task RollbackIncludesTransitivePins()
    {
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();
        CreateBackupFile(propsPath, "backup");

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        AddTransitiveUpdate("System.Text.Encodings.Web", "7.0.0", "7.0.1");
        _candidates.TransitivePackagesFound = 1;

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = propsPath, BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("FAILED", false));

        var result = await _sut.UpdatePackagesAsync(CreateOptions(includeTransitive: true));

        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        result.WasRolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task NoTransitiveFlag_DoesNotScanTransitive()
    {
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        AddTransitiveUpdate("System.Text.Encodings.Web", "7.0.0", "7.0.1");

        SetupBackupAndBuild();

        var result = await _sut.UpdatePackagesAsync(CreateOptions(includeTransitive: false));

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesFound.Should().Be(0);
        _candidates.LastIncludeTransitive.Should().BeFalse();
        result.Updates.Should().NotContain(u => u.IsTransitive);
    }

    private void SetupProjectAnalyzer()
    {
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        _projectAnalyzerMock.Setup(p => p.DiscoverProjectsFromSolutionAsync(It.IsAny<string>()))
            .ReturnsAsync((_testDirectory, new List<string> { projectPath }));
    }

    private void AddTransitiveUpdate(string packageName, string current, string latest)
    {
        var isMajor = NuGet.Versioning.NuGetVersion.Parse(latest).Major
            != NuGet.Versioning.NuGetVersion.Parse(current).Major;
        _candidates.ExtraUpdates.Add(
            new PackageUpdateEntry(packageName, current, latest, isMajor, !isMajor, IsTransitive: true));
    }

    private void SetupBackupAndBuild()
    {
        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry
            {
                OriginalPath = Path.Combine(_testDirectory, "Directory.Packages.props"),
                BackupFileName = "backup"
            });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("Tests passed", true));
    }

    private string CreatePropsFile(params (string Name, string Version)[] packages)
    {
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var items = string.Join("\n", packages.Select(p =>
            $"    <PackageVersion Include=\"{p.Name}\" Version=\"{p.Version}\" />"));

        File.WriteAllText(propsPath, $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
            {items}
              </ItemGroup>
            </Project>
            """);

        return propsPath;
    }

    private void CreateSolutionFile()
    {
        File.WriteAllText(Path.Combine(_testDirectory, "Test.sln"), "");
    }

    private void CreateBackupFile(string originalPath, string backupFileName)
    {
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);
        File.Copy(originalPath, Path.Combine(backupDir, backupFileName), overwrite: true);
    }

    private Options CreateOptions(bool dryRun = false, bool includePrerelease = false, bool includeTransitive = false)
    {
        return new Options
        {
            SolutionFileDir = _testDirectory,
            UpdatePackages = true,
            DryRun = dryRun,
            IncludePrerelease = includePrerelease,
            IncludeTransitive = includeTransitive,
            BackupDir = _testDirectory,
            NoBackup = false
        };
    }
}
