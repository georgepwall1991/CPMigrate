using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services;

public class PackageUpdateServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _consoleService;
    private readonly Mock<IProjectAnalyzer> _projectAnalyzerMock;
    private readonly Mock<INuGetVersionLookupService> _nuGetLookupMock;
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
        _nuGetLookupMock = new Mock<INuGetVersionLookupService>();
        _nuGetLookupMock.SetupGet(x => x.FailedLookups).Returns(Array.Empty<string>());
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

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

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

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

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

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

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

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("14.0.3"));

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

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("14.0.3"));

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

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

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

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("Newtonsoft.Json", false))
            .ReturnsAsync(NuGetVersion.Parse("13.0.3"));

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
    public async Task UpdatePackagesAsync_NuGetLookupFails_SkipsPackage()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("WorkingPackage", "1.0.0"), ("BrokenPackage", "1.0.0"));
        CreateSolutionFile();

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("WorkingPackage", false))
            .ReturnsAsync(NuGetVersion.Parse("2.0.0"));
        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("BrokenPackage", false))
            .ReturnsAsync((NuGetVersion?)null);

        // Accept the major update for WorkingPackage
        _consoleService.SelectionResponses.Enqueue("Accept major update to 2.0.0");

        _backupManagerMock.Setup(b => b.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new BackupEntry { OriginalPath = Path.Combine(_testDirectory, "Directory.Packages.props"), BackupFileName = "backup" });

        _dotNetCliMock.Setup(d => d.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync(("Restored", true));
        _dotNetCliMock.Setup(d => d.RunTestAsync(It.IsAny<string>()))
            .ReturnsAsync(("Passed", true));

        var options = CreateOptions();

        // Act
        var result = await _sut.UpdatePackagesAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.Success);
        // BrokenPackage was skipped, only WorkingPackage was updated
        result.Updates.Should().NotContain(u => u.PackageName == "BrokenPackage");
    }

    [Fact]
    public async Task UpdatePackagesAsync_IncludePrerelease_PassedToNuGetLookup()
    {
        // Arrange
        SetupProjectAnalyzer();
        CreatePropsFile(("TestPkg", "1.0.0"));

        _nuGetLookupMock.Setup(n => n.GetLatestVersionAsync("TestPkg", true))
            .ReturnsAsync(NuGetVersion.Parse("1.0.0"));

        var options = CreateOptions(includePrerelease: true);

        // Act
        await _sut.UpdatePackagesAsync(options);

        // Assert
        _nuGetLookupMock.Verify(n => n.GetLatestVersionAsync("TestPkg", true), Times.Once);
    }

    [Fact]
    public void Dispose_DisposesLookupService_WhenLookupOwnsResources()
    {
        // Arrange
        var disposableLookup = new DisposableNuGetVersionLookupService();
        var service = new PackageUpdateService(
            _consoleService,
            _projectAnalyzerMock.Object,
            _propsGenerator,
            disposableLookup,
            _dotNetCliMock.Object,
            _backupManagerMock.Object);

        // Act
        service.Dispose();

        // Assert
        disposableLookup.Disposed.Should().BeTrue();
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

    private sealed class DisposableNuGetVersionLookupService : INuGetVersionLookupService, IDisposable
    {
        public bool Disposed { get; private set; }

        public IReadOnlyCollection<string> FailedLookups => Array.Empty<string>();

        public Task<NuGetVersion?> GetLatestVersionAsync(string packageId, bool includePrerelease = false)
        {
            return Task.FromResult<NuGetVersion?>(null);
        }

        public Task<NuGetVersion?> GetLatestVersionInMajorAsync(string packageId, int majorVersion, bool includePrerelease = false)
        {
            return Task.FromResult<NuGetVersion?>(null);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
