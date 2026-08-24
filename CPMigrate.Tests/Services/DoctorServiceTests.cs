using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for the doctor's disk-space and write-access checks: threshold classification,
/// write probing against real and missing directories, and graceful degradation when
/// the environment cannot answer.
/// </summary>
public class DoctorServiceTests : IDisposable
{
    private const long Kilobyte = 1024;
    private const long Megabyte = Kilobyte * 1024;
    private const long Gigabyte = Megabyte * 1024;

    private readonly string _testDirectory;

    public DoctorServiceTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateDoctorTest_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region Disk Space Classification

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(199 * Megabyte)]
    [InlineData(200 * Megabyte - 1)]
    public void ClassifyDiskSpace_BelowErrorThreshold_ReturnsError(long freeBytes)
    {
        var check = DoctorService.ClassifyDiskSpace(freeBytes);

        check.Name.Should().Be("Disk");
        check.Status.Should().Be(DoctorStatus.Error);
        check.Details.Should().Contain("free");
        check.Hint.Should().NotBeNull();
        check.Hint.Should().Contain(".cpmigrate_backup");
    }

    [Theory]
    [InlineData(200 * Megabyte)]
    [InlineData(200 * Megabyte + 1)]
    [InlineData(Gigabyte)]
    [InlineData(2 * Gigabyte - 1)]
    public void ClassifyDiskSpace_AtOrAboveErrorThresholdBelowWarningThreshold_ReturnsWarning(long freeBytes)
    {
        var check = DoctorService.ClassifyDiskSpace(freeBytes);

        check.Status.Should().Be(DoctorStatus.Warning);
        check.Hint.Should().NotBeNull();
    }

    [Theory]
    [InlineData(2 * Gigabyte)]
    [InlineData(2 * Gigabyte + 1)]
    [InlineData(50 * Gigabyte)]
    [InlineData(long.MaxValue / 2)]
    public void ClassifyDiskSpace_AtOrAboveWarningThreshold_ReturnsOkWithoutHint(long freeBytes)
    {
        var check = DoctorService.ClassifyDiskSpace(freeBytes);

        check.Status.Should().Be(DoctorStatus.Ok);
        check.Hint.Should().BeNull();
    }

    [Fact]
    public void ClassifyDiskSpace_FormatsGigabytesReadable()
    {
        var check = DoctorService.ClassifyDiskSpace(3 * Gigabyte + 512 * Megabyte);

        check.Details.Should().Contain("GB");
    }

    #endregion

    #region Write Access Probe

    [Fact]
    public void ProbeWriteAccess_WritableDirectory_ReturnsOkAndCleansUpProbeFile()
    {
        var check = DoctorService.ProbeWriteAccess(_testDirectory);

        check.Name.Should().Be("Write");
        check.Status.Should().Be(DoctorStatus.Ok);
        Directory.EnumerateFiles(_testDirectory).Should().BeEmpty();
    }

    [Fact]
    public void ProbeWriteAccess_NonexistentDirectory_ReturnsInfoSkip()
    {
        var missing = Path.Combine(_testDirectory, "does-not-exist");

        var check = DoctorService.ProbeWriteAccess(missing);

        check.Status.Should().Be(DoctorStatus.Info);
        check.Details.Should().Contain(missing);
    }

    [Fact]
    public void CheckWriteAccess_NonexistentSearchPath_ProbesHostingDirectory()
    {
        // A missing search path resolves to its nearest existing parent directory,
        // so writability of that parent is what gets probed.
        var check = DoctorService.CheckWriteAccess(Path.Combine(_testDirectory, "missing"));

        check.Status.Should().Be(DoctorStatus.Ok);
    }

    [Fact]
    public void CheckWriteAccess_WritableWorkspace_ReturnsOk()
    {
        var check = DoctorService.CheckWriteAccess(_testDirectory);

        check.Status.Should().Be(DoctorStatus.Ok);
    }

    #endregion

    #region Disk Space Against Real Volume

    [Fact]
    public void CheckDiskSpace_RealWorkspaceVolume_ReportsUsableFreeSpace()
    {
        var check = DoctorService.CheckDiskSpace(_testDirectory);

        // The volume exists, so the check must classify rather than degrade to Info.
        check.Name.Should().Be("Disk");
        check.Status.Should().NotBe(DoctorStatus.Info);
        check.Details.Should().Contain("free");
    }

    [Fact]
    public void CheckDiskSpace_FilePath_ResolvesToHostingVolume()
    {
        var filePath = Path.Combine(_testDirectory, "some-project.csproj");
        File.WriteAllText(filePath, string.Empty);

        var check = DoctorService.CheckDiskSpace(filePath);

        check.Status.Should().NotBe(DoctorStatus.Info);
    }

    #endregion
}
