using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services;

public class PackageUpdateServiceTransitiveTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _consoleService;
    private readonly Mock<IProjectAnalyzer> _projectAnalyzerMock;
    private readonly Mock<INuGetVersionLookupService> _nuGetLookupMock;
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
        _nuGetLookupMock = new Mock<INuGetVersionLookupService>();
        _nuGetLookupMock.Setup(x => x.GetFailedLookups()).Returns(Array.Empty<string>());
        _dotNetCliMock = new Mock<IDotNetCliService>();
        _backupManagerMock = new Mock<IBackupManager>();
        _propsGenerator = new PropsGenerator();

        _sut = new PackageUpdateService(
            _consoleService,
            _projectAnalyzerMock.Object,
            _propsGenerator,
            _nuGetLookupMock.Object,
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
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();

        // Direct package is up to date
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        // Transitive dep has an update
        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("8.0.0"));

        // Accept major update for transitive
        _consoleService.SelectionResponses.Enqueue("Accept major update to 8.0.0");

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: true);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesFound.Should().BeGreaterThan(0);
        result.TransitivePackagesUpdated.Should().Be(1);
        result.Updates.Should().Contain(u =>
            u.PackageName == "System.Text.Encodings.Web" && u.IsTransitive && u.Accepted);
    }

    [Fact]
    public async Task TransitiveDepsExcludedIfAlreadyDirect()
    {
        // Arrange — Newtonsoft.Json is both a direct dep and appears as transitive
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        // Transitive scan returns Newtonsoft.Json (same as direct)
        SetupTransitiveScan("Newtonsoft.Json", "13.0.1");

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: true);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert — should only have the direct update, not a duplicate transitive entry
        result.Updates.Where(u => u.PackageName == "Newtonsoft.Json").Should().HaveCount(1);
        result.Updates.Should().NotContain(u => u.PackageName == "Newtonsoft.Json" && u.IsTransitive);
    }

    [Fact]
    public async Task TransitiveDryRun_ShowsBothSections()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("7.0.1"));

        var options = CreateOptions(dryRun: true, includeTransitive: true);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PackagesUpdated.Should().Be(1); // direct
        result.TransitivePackagesUpdated.Should().Be(1); // transitive
        result.Updates.Should().Contain(u => u.PackageName == "Newtonsoft.Json" && !u.IsTransitive);
        result.Updates.Should().Contain(u => u.PackageName == "System.Text.Encodings.Web" && u.IsTransitive);
    }

    [Fact]
    public async Task TransitiveUpdatesPinnedInProps()
    {
        // Arrange
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();

        // Direct is up to date
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        // Transitive has minor update (auto-accepted)
        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("7.0.1"));

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: true);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesUpdated.Should().Be(1);

        // Verify the props file now contains the transitive pin
        var propsContent = await File.ReadAllTextAsync(propsPath);
        propsContent.Should().Contain("System.Text.Encodings.Web");
        propsContent.Should().Contain("7.0.1");
    }

    [Fact]
    public async Task TransitiveScanFailure_SkipsGracefully()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        // All transitive scans fail
        _projectAnalyzerMock
            .Setup(p => p.ScanTransitivePackagesAsync(It.IsAny<string>()))
            .ReturnsAsync((new List<PackageReference>(), false));

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: true);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert — should still succeed with direct updates
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesFound.Should().Be(0);
        result.TransitivePackagesUpdated.Should().Be(0);
        _consoleService.OutputMessages.Should().Contain(m => m.Contains("Could not scan transitive"));
    }

    [Fact]
    public async Task RollbackIncludesTransitivePins()
    {
        // Arrange
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();
        CreateBackupFile(propsPath, "backup");

        // Direct is up to date
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        // Transitive has update
        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("7.0.1"));

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = propsPath, BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("FAILED", false));

        var options = CreateOptions(includeTransitive: true);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        result.WasRolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task TransitivePinningOff_WithholdsInertPins()
    {
        // Without CentralPackageTransitivePinningEnabled a PackageVersion for a package nothing
        // references directly is a dead line restore ignores — writing it and reporting "updated"
        // would claim coverage the graph does not have. The run reports instead of writes.
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(enableTransitivePinning: false, ("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("7.0.1"));

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: true);

        var result = await _sut.UpdatePackagesAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesUpdated.Should().Be(0);
        result.TransitivePackagesWithheld.Should().Be(1);
        result.Updates.Should().ContainSingle(u => u.PackageName == "System.Text.Encodings.Web")
            .Which.Withheld.Should().BeTrue();
        File.ReadAllText(propsPath).Should().NotContain("System.Text.Encodings.Web",
            "an inert pin is not written");
        _consoleService.OutputMessages.Should().Contain(m =>
            m.Contains("withheld") && m.Contains("CentralPackageTransitivePinningEnabled"));
    }

    [Fact]
    public async Task TransitivePinningOff_OnlyDirectUpdatesApply()
    {
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(enableTransitivePinning: false, ("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("7.0.1"));

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: true);

        var result = await _sut.UpdatePackagesAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PackagesUpdated.Should().Be(1, "the direct update still applies");
        result.TransitivePackagesUpdated.Should().Be(0);
        result.TransitivePackagesWithheld.Should().Be(1);

        var propsContent = File.ReadAllText(propsPath);
        propsContent.Should().Contain("13.0.3", "the direct update was written");
        propsContent.Should().NotContain("System.Text.Encodings.Web");
    }

    [Fact]
    public async Task TransitivePinningOff_DryRunPreviewExcludesWithheldPins()
    {
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(enableTransitivePinning: false, ("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("7.0.1"));

        var options = CreateOptions(dryRun: true, includeTransitive: true);

        var result = await _sut.UpdatePackagesAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesWithheld.Should().Be(1);
        // The preview owes the same honesty the write does — a withheld pin never appears in it.
        _consoleService.PropsPreviews.Should().ContainSingle()
            .Which.Should().Contain("13.0.3").And.NotContain("System.Text.Encodings.Web");
        File.ReadAllText(propsPath).Should().NotContain("13.0.3");
    }

    [Fact]
    public async Task TransitivePinningViaBuildProps_AppliesTransitively()
    {
        // The property is a build property like any other — set in Directory.Build.props it opts the
        // workspace in the same as setting it in the packages file.
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(enableTransitivePinning: false, ("Newtonsoft.Json", "13.0.3"));
        CreateSolutionFile();
        File.WriteAllText(
            Path.Combine(_testDirectory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        SetupTransitiveScan("System.Text.Encodings.Web", "7.0.0");
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("System.Text.Encodings.Web", false))
            .ReturnsAsync(NuGetVersion.Parse("7.0.1"));

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: true);

        var result = await _sut.UpdatePackagesAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesUpdated.Should().Be(1);
        result.TransitivePackagesWithheld.Should().Be(0);
        File.ReadAllText(propsPath).Should().Contain("System.Text.Encodings.Web");
    }

    [Fact]
    public async Task NoTransitiveFlag_DoesNotScanTransitive()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

        SetupBackupAndBuild();

        var options = CreateOptions(includeTransitive: false);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TransitivePackagesFound.Should().Be(0);
        _projectAnalyzerMock.Verify(
            p => p.ScanTransitivePackagesAsync(It.IsAny<string>()), Times.Never);
    }

    #region Helpers

    private void SetupProjectAnalyzer()
    {
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        _projectAnalyzerMock.Setup(p => p.DiscoverProjectsFromSolutionAsync(It.IsAny<string>()))
            .ReturnsAsync((_testDirectory, new List<string> { projectPath }));
    }

    private void SetupTransitiveScan(string packageName, string version)
    {
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        _projectAnalyzerMock
            .Setup(p => p.ScanTransitivePackagesAsync(projectPath))
            .ReturnsAsync((new List<PackageReference>
            {
                new(packageName, version, projectPath, "Test.csproj", IsTransitive: true)
            }, true));
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

    private string CreatePropsFile(params (string Name, string Version)[] packages) =>
        CreatePropsFile(enableTransitivePinning: true, packages);

    private string CreatePropsFile(bool enableTransitivePinning, params (string Name, string Version)[] packages)
    {
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        var items = string.Join("\n", packages.Select(p =>
            $"    <PackageVersion Include=\"{p.Name}\" Version=\"{p.Version}\" />"));
        var pinning = enableTransitivePinning
            ? "\n    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>"
            : "";

        File.WriteAllText(propsPath, $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>{pinning}
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
        var backupFilePath = Path.Combine(backupDir, backupFileName);
        File.Copy(originalPath, backupFilePath, overwrite: true);
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

    #endregion
}
