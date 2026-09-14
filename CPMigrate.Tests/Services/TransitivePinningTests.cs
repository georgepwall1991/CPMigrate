using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class TransitivePinningTests : IDisposable
{
    private readonly string _directory;
    private readonly string _propsPath;

    public TransitivePinningTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"CPMigratePin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _propsPath = Path.Combine(_directory, "Directory.Packages.props");
        File.WriteAllText(_propsPath, "<Project />");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IsEnabled_PropertyTrueInProps_ReturnsTrue()
    {
        WritePropsProperty("true");

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_PropertyAbsent_ReturnsFalse()
    {
        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_PropertyFalse_ReturnsFalse()
    {
        WritePropsProperty("false");

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_LastAssignmentWins()
    {
        // MSBuild resolves a repeated assignment last-wins; an earlier true overridden by a later
        // false is off.
        File.WriteAllText(_propsPath,
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
                <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_MalformedProps_ReturnsFalse()
    {
        // Unreadable means unproven, and an unproven pin is one that might do nothing.
        File.WriteAllText(_propsPath, "<Project><PropertyGroup><CentralPackageTransitive");

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_MissingProps_ReturnsFalse()
    {
        File.Delete(_propsPath);

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_PropertyInBuildProps_ReturnsTrue()
    {
        File.WriteAllText(
            Path.Combine(_directory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_BuildPropsAboveScanRoot_ReturnsTrue()
    {
        // MSBuild walks up from each project for Directory.Build.props — the walk here starts at
        // the scan root, so a build props beside the props file above it still governs.
        var nested = Path.Combine(_directory, "src", "App");
        Directory.CreateDirectory(nested);
        File.WriteAllText(
            Path.Combine(_directory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        TransitivePinning.IsEnabled(_propsPath, nested).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_PropsFalseBeatsBuildPropsTrue_ReturnsFalse()
    {
        // MSBuild imports Directory.Build.props before Directory.Packages.props, so an explicit
        // assignment in the props file wins over the earlier build-props one.
        File.WriteAllText(
            Path.Combine(_directory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);
        WritePropsProperty("false");

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_BuildPropsBesidePropsFile_NoScanRoot_ReturnsTrue()
    {
        // With no scan root to walk from, the props file's own directory is still checked — the
        // build props that sits next to it governs the same projects.
        File.WriteAllText(
            Path.Combine(_directory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        TransitivePinning.IsEnabled(_propsPath, null).Should().BeTrue();
    }

    [Fact]
    public void TryRead_PropertyAbsent_ReturnsNull()
    {
        TransitivePinning.TryRead(_propsPath).Should().BeNull();
    }

    [Fact]
    public void TryRead_PropertyFalse_ReturnsFalseNotNull()
    {
        // "explicitly off" is a different answer from "never set" — migration only needs to act on
        // the latter.
        WritePropsProperty("false");

        TransitivePinning.TryRead(_propsPath).Should().BeFalse();
    }

    [Fact]
    public void TryRead_WhitespaceValue_ReturnsFalseNotNull()
    {
        // An empty assignment is not "true" under MSBuild semantics, so it reads as off rather than
        // unset — the caller must not mistake it for a missing setting.
        WritePropsProperty("   ");

        TransitivePinning.TryRead(_propsPath).Should().BeFalse();
    }

    [Fact]
    public void TryRead_CaseInsensitiveTrue_ReturnsTrue()
    {
        WritePropsProperty("TRUE");

        TransitivePinning.TryRead(_propsPath).Should().BeTrue();
    }

    private void WritePropsProperty(string value)
    {
        File.WriteAllText(_propsPath,
            $"""
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);
    }
}
