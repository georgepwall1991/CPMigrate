using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services;

public class PackageUpdateServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _consoleService;
    private readonly Mock<IProjectAnalyzer> _projectAnalyzerMock;
    private readonly FakeUpdateCandidateSource _candidates = new();
    private readonly Mock<IDotNetCliService> _dotNetCliMock;
    private readonly Mock<IBackupManager> _backupManagerMock;
    private readonly PropsGenerator _propsGenerator;
    private readonly PackageUpdateService _sut;

    public PackageUpdateServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateUpdateTest_{Guid.NewGuid():N}");
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
    public async Task UpdatePackagesAsync_NoPropsFile_ReturnsValidationError()
    {
        // Arrange
        SetupProjectAnalyzer();
        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.ValidationError);
        _consoleService.ErrorMessages.Should().Contain(m => m.Contains("Directory.Packages.props not found"));
    }

    [Fact]
    public async Task UpdatePackagesAsync_AllUpToDate_ReturnsSuccess()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.3"));

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");

        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        _consoleService.OutputMessages.Should().Contain(m => m.Contains("up to date"));
    }

    [Fact]
    public async Task UpdatePackagesAsync_DryRun_DoesNotModifyFiles()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "12.0.3"));

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");

        var options = CreateOptions(dryRun: true);

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PackagesUpdated.Should().Be(1);

        // Verify the props file was NOT modified
        var propsContent = await File.ReadAllTextAsync(Path.Combine(_testDirectory, "Directory.Packages.props"));
        propsContent.Should().Contain("12.0.3");
        propsContent.Should().NotContain("13.0.3");
    }

    [Fact]
    public async Task UpdatePackagesAsync_MinorUpdate_AutoAccepted()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = Path.Combine(_testDirectory, "Directory.Packages.props"), BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("Tests passed", true));

        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.TestsPassed.Should().BeTrue();
        result.PackagesUpdated.Should().Be(1);
    }

    [Fact]
    public async Task UpdatePackagesAsync_MajorUpdate_PromptsUser()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "12.0.3"));
        CreateSolutionFile();

        _candidates.SetLatest("Newtonsoft.Json", "14.0.3");

        // User accepts the major update (first option)
        _consoleService.SelectionResponses.Enqueue("Accept major update to 14.0.3");

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = Path.Combine(_testDirectory, "Directory.Packages.props"), BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("Tests passed", true));

        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Updates.Should().Contain(u => u.PackageName == "Newtonsoft.Json" && u.Accepted);
    }

    [Fact]
    public async Task UpdatePackagesAsync_MajorUpdateSkipped_NotApplied()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "12.0.3"));

        _candidates.SetLatest("Newtonsoft.Json", "14.0.3");

        // User skips the major update
        _consoleService.SelectionResponses.Enqueue("Skip this package");

        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Updates.Should().Contain(u => u.PackageName == "Newtonsoft.Json" && !u.Accepted);
    }

    [Fact]
    public async Task UpdatePackagesAsync_TestsFail_RollsBack()
    {
        // Arrange
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();
        var backupFilePath = CreateBackupFile(propsPath, "backup");

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = propsPath, BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("FAILED: Some.Test", false));

        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        result.TestsPassed.Should().BeFalse();
        result.WasRolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task UpdatePackagesAsync_RestoreFails_RollsBack()
    {
        // Arrange
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        CreateSolutionFile();
        var backupFilePath = CreateBackupFile(propsPath, "backup");

        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = propsPath, BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restore failed", false));

        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        result.WasRolledBack.Should().BeTrue();
        _dotNetCliMock.Verify(d => d.RunTestAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePackagesAsync_PackageAbsentFromOutdatedScan_IsNotProposed()
    {
        // A pin whose package never appears in the restore-backed outdated rows is not a
        // candidate — that is how a private-feed package used to look "up to date" when
        // nuget.org 404'd it, and how a squatted nuget.org version is now refused.
        SetupProjectAnalyzer();
        CreatePropsFile(("WorkingPackage", "1.0.0"), ("BrokenPackage", "1.0.0"));
        CreateSolutionFile();

        _candidates.SetLatest("WorkingPackage", "2.0.0");

        _consoleService.SelectionResponses.Enqueue("Accept major update to 2.0.0");

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = Path.Combine(_testDirectory, "Directory.Packages.props"), BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("Passed", true));

        var options = CreateOptions();

        var result = await _sut.UpdatePackagesAsync(options);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Updates.Should().NotContain(u => u.PackageName == "BrokenPackage");
    }

    [Fact]
    public async Task UpdatePackagesAsync_IncludePrerelease_PassedToCandidateSource()
    {
        SetupProjectAnalyzer();
        CreatePropsFile(("TestPkg", "1.0.0"));
        _candidates.SetLatest("TestPkg", "1.0.0");

        var options = CreateOptions(includePrerelease: true);

        await _sut.UpdatePackagesAsync(options);

        _candidates.LastIncludePrerelease.Should().BeTrue();
        _candidates.FindCalls.Should().Be(1);
    }

    [Fact]
    public async Task UpdatePackagesAsync_UnscannedProject_ExitsIncompleteAndWritesNothing()
    {
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        var original = await File.ReadAllTextAsync(propsPath);
        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        _candidates.UnscannedProjects.Add("Broken.csproj");

        var result = await _sut.UpdatePackagesAsync(CreateOptions());

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
        result.Warnings.Should().Contain(w => w.Contains("Broken.csproj"));
        result.Warnings.Should().Contain(w => w.Contains("cannot be updated safely"));
        _consoleService.ErrorMessages.Should().Contain(m => m.Contains("Broken.csproj"));
        (await File.ReadAllTextAsync(propsPath)).Should().Be(original);
        _backupManagerMock.Verify(
            b => b.CreateBackupForProject(
                It.IsAny<BackupSettings>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>()),
            Times.Never);
        _backupManagerMock.Verify(
            b => b.CreateBackupForProject(
                It.IsAny<Options>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePackagesAsync_UnscannedProject_DryRunAlsoExitsIncomplete()
    {
        SetupProjectAnalyzer();
        var propsPath = CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        var original = await File.ReadAllTextAsync(propsPath);
        _candidates.SetLatest("Newtonsoft.Json", "13.0.3");
        _candidates.UnscannedProjects.Add("Broken.csproj");

        var result = await _sut.UpdatePackagesAsync(CreateOptions(dryRun: true));

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
        result.Warnings.Should().NotBeNull();
        (await File.ReadAllTextAsync(propsPath)).Should().Be(original);
    }

    [Fact]
    public async Task UpdatePackagesAsync_UnscannedProject_JsonOutputCarriesWarning()
    {
        SetupProjectAnalyzer();
        CreatePropsFile(("Newtonsoft.Json", "13.0.1"));
        _candidates.UnscannedProjects.Add("Api.csproj");

        var options = CreateOptions();
        options.Output = OutputFormat.Json;

        var result = await _sut.UpdatePackagesAsync(options);

        result.ExitCode.Should().Be(ExitCodes.IncompleteAnalysis);
        result.Warnings.Should().Contain(w => w.Contains("Api.csproj"));
    }

    private void SetupProjectAnalyzer()
    {
        _projectAnalyzerMock.Setup(p => p.DiscoverProjectsFromSolutionAsync(It.IsAny<string>()))
            .ReturnsAsync((_testDirectory, new List<string> { Path.Combine(_testDirectory, "Test.csproj") }));
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

    private string CreateBackupFile(string originalPath, string backupFileName)
    {
        // BackupManager.CreateBackupDirectory creates .cpmigrate_backup under BackupDir
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);
        var backupFilePath = Path.Combine(backupDir, backupFileName);
        File.Copy(originalPath, backupFilePath, overwrite: true);
        return backupFilePath;
    }

    private Options CreateOptions(bool dryRun = false, bool includePrerelease = false)
    {
        return new Options
        {
            SolutionFileDir = _testDirectory,
            UpdatePackages = true,
            DryRun = dryRun,
            IncludePrerelease = includePrerelease,
            BackupDir = _testDirectory,
            NoBackup = false
        };
    }
}
