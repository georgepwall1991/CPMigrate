using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services.Update;

/// <summary>
/// End-to-end coverage of <c>--update-packages</c> driven through the real
/// <see cref="BackupManager"/> rather than a mock, so backup wiring is exercised for real.
/// </summary>
public class PackageUpdateServiceBisectTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console = new();
    private readonly Mock<IProjectAnalyzer> _projectAnalyzer = new();
    private readonly Mock<INuGetVersionLookupService> _nuGet = CreateLookupMock();

    /// <summary>
    /// The interface promises a non-null FailedLookups; Moq would return null for it.
    /// </summary>
    private static Mock<INuGetVersionLookupService> CreateLookupMock()
    {
        var mock = new Mock<INuGetVersionLookupService>();
        mock.Setup(x => x.GetFailedLookups()).Returns(Array.Empty<string>());
        return mock;
    }
    private readonly Mock<IDotNetCliService> _cli = new();
    private readonly PackageUpdateService _sut;
    private readonly string _propsPath;

    public PackageUpdateServiceBisectTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateBisect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");

        _projectAnalyzer.Setup(p => p.DiscoverProjectsFromSolutionAsync(It.IsAny<string>()))
            .ReturnsAsync((_testDirectory, new List<string> { Path.Combine(_testDirectory, "Test.csproj") }));
        _cli.Setup(c => c.RunRestoreAsync(It.IsAny<string>())).ReturnsAsync((string.Empty, true));

        _sut = new PackageUpdateService(
            _console,
            _projectAnalyzer.Object,
            new PropsGenerator(),
            _nuGet.Object,
            _cli.Object,
            new BackupManager());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Regression test for a backup call whose arguments were shifted, writing the backup into a directory
    /// named after the timestamp. With a mocked backup manager the mismatch was invisible; with the real
    /// one it throws.
    /// </summary>
    [Fact]
    public async Task UpdatePackagesAsync_WithRealBackupManager_WritesBackupIntoTheBackupDirectory()
    {
        WriteProps(("Alpha", "1.0.0"));
        WriteSolution();
        SetLatest("Alpha", "1.1.0");
        SetTestOutcome(_ => true);

        var result = await _sut.UpdatePackagesAsync(CreateOptions());

        result.ExitCode.Should().Be(ExitCodes.Success);
        // A backup directory named after a timestamp is the signature of the old argument-order bug.
        Directory.GetDirectories(_testDirectory)
            .Select(Path.GetFileName)
            .Should().NotContain(name => name!.Length == 17 && name.All(char.IsDigit));
    }

    [Fact]
    public async Task UpdatePackagesAsync_Bisect_KeepsGoodUpdatesAndHoldsBackTheCulprit()
    {
        WriteProps(("Alpha", "1.0.0"), ("Beta", "1.0.0"), ("Gamma", "1.0.0"), ("Delta", "1.0.0"));
        WriteSolution();
        SetLatest("Alpha", "1.1.0");
        SetLatest("Beta", "1.1.0");
        SetLatest("Gamma", "1.1.0");
        SetLatest("Delta", "1.1.0");

        // Gamma is the poison pill: any props file containing Gamma 1.1.0 fails its tests.
        SetTestOutcome(props => !props.Contains("Include=\"Gamma\" Version=\"1.1.0\""));

        var result = await _sut.UpdatePackagesAsync(CreateOptions(bisect: true));

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PackagesUpdated.Should().Be(3);
        result.PackagesHeldBack.Should().Be(1);
        result.TestsPassed.Should().BeTrue();
        result.WasRolledBack.Should().BeFalse();
        result.VerificationRuns.Should().BeGreaterThan(1);
        result.Updates.Should().Contain(u => u.PackageName == "Gamma" && u.HeldBack);
        result.Updates.Where(u => u.PackageName != "Gamma").Should().OnlyContain(u => !u.HeldBack);

        // The file on disk must match what was reported.
        var finalProps = await File.ReadAllTextAsync(_propsPath);
        finalProps.Should().Contain("Include=\"Gamma\" Version=\"1.0.0\"");
        finalProps.Should().Contain("Include=\"Alpha\" Version=\"1.1.0\"");
    }

    [Fact]
    public async Task UpdatePackagesAsync_WithoutBisect_StillRollsEverythingBack()
    {
        WriteProps(("Alpha", "1.0.0"), ("Gamma", "1.0.0"));
        WriteSolution();
        SetLatest("Alpha", "1.1.0");
        SetLatest("Gamma", "1.1.0");
        SetTestOutcome(props => !props.Contains("Include=\"Gamma\" Version=\"1.1.0\""));

        var result = await _sut.UpdatePackagesAsync(CreateOptions());

        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        result.WasRolledBack.Should().BeTrue();
        result.PackagesUpdated.Should().Be(0);

        var finalProps = await File.ReadAllTextAsync(_propsPath);
        finalProps.Should().Contain("Include=\"Alpha\" Version=\"1.0.0\"");
        finalProps.Should().NotContain("1.1.0");
    }

    [Fact]
    public async Task UpdatePackagesAsync_BisectWithEverythingBroken_RevertsAndFails()
    {
        WriteProps(("Alpha", "1.0.0"), ("Beta", "1.0.0"));
        WriteSolution();
        SetLatest("Alpha", "1.1.0");
        SetLatest("Beta", "1.1.0");
        SetTestOutcome(props => !props.Contains("1.1.0"));

        var result = await _sut.UpdatePackagesAsync(CreateOptions(bisect: true));

        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        result.PackagesUpdated.Should().Be(0);
        result.PackagesHeldBack.Should().Be(2);
        (await File.ReadAllTextAsync(_propsPath)).Should().NotContain("1.1.0");
    }

    [Fact]
    public async Task UpdatePackagesAsync_RestoreFails_SaysRestoreNotTests()
    {
        // Tests never ran, so reporting "Tests failed" would send the user hunting in the wrong place.
        WriteProps(("Alpha", "1.0.0"));
        WriteSolution();
        SetLatest("Alpha", "1.1.0");
        _cli.Setup(c => c.RunRestoreAsync(It.IsAny<string>())).ReturnsAsync(("NU1605 downgrade", false));

        var result = await _sut.UpdatePackagesAsync(CreateOptions());

        result.ExitCode.Should().Be(ExitCodes.TestFailure);
        _console.ErrorMessages.Should().Contain(m => m.Contains("dotnet restore failed"));
        _console.ErrorMessages.Should().NotContain(m => m.Contains("Tests failed"));
        _cli.Verify(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePackagesAsync_BisectTestFilter_IsForwardedToDotNetTest()
    {
        WriteProps(("Alpha", "1.0.0"));
        WriteSolution();
        SetLatest("Alpha", "1.1.0");
        SetTestOutcome(_ => true);

        await _sut.UpdatePackagesAsync(CreateOptions(bisect: true, testFilter: "Category=Fast"));

        _cli.Verify(c => c.RunTestAsync(It.IsAny<string>(), "Category=Fast"), Times.Once);
    }

    [Fact]
    public async Task UpdatePackagesAsync_OnlyFilter_RestrictsCandidatesAndWarnsOnUnknownName()
    {
        WriteProps(("Alpha", "1.0.0"), ("Beta", "1.0.0"));
        WriteSolution();
        SetLatest("Alpha", "1.1.0");
        SetLatest("Beta", "1.1.0");
        SetTestOutcome(_ => true);

        var options = CreateOptions();
        options.Only = "Alpha,NotAPackage";

        var result = await _sut.UpdatePackagesAsync(options);

        result.PackagesUpdated.Should().Be(1);
        result.Updates.Should().OnlyContain(u => u.PackageName == "Alpha");
        _console.OutputMessages.Should().Contain(m => m.Contains("NotAPackage"));

        var finalProps = await File.ReadAllTextAsync(_propsPath);
        finalProps.Should().Contain("Include=\"Beta\" Version=\"1.0.0\"");
    }

    private void SetLatest(string package, string version) =>
        _nuGet.Setup(n => n.GetLatestVersionAsync(package, It.IsAny<bool>()))
            .ReturnsAsync(NuGetVersion.Parse(version));

    /// <summary>
    /// Decides pass/fail from the props file as it stands on disk, which is what a real test run reacts to.
    /// </summary>
    private void SetTestOutcome(Func<string, bool> passesForProps)
    {
        _cli.Setup(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() =>
            {
                var props = File.ReadAllText(_propsPath);
                return passesForProps(props) ? (string.Empty, true) : ("FAILED", false);
            });
    }

    private void WriteProps(params (string Name, string Version)[] packages)
    {
        var items = string.Join(
            Environment.NewLine,
            packages.Select(p => $"    <PackageVersion Include=\"{p.Name}\" Version=\"{p.Version}\" />"));

        File.WriteAllText(_propsPath,
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
            {items}
              </ItemGroup>
            </Project>
            """);
    }

    private void WriteSolution() => File.WriteAllText(Path.Combine(_testDirectory, "Test.sln"), string.Empty);

    private Options CreateOptions(bool bisect = false, string? testFilter = null) => new()
    {
        SolutionFileDir = _testDirectory,
        UpdatePackages = true,
        BackupDir = _testDirectory,
        NoBackup = false,
        Bisect = bisect,
        BisectTestFilter = testFilter
    };
}
