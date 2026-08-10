using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>&lt;PackageReference Update="X" /&gt;</c> amends a reference rather than adding one. Recording it
/// as a second declaration makes <c>RedundantReference</c> report a duplicate that does not exist,
/// and leaves the superseded version in the list for <c>FloatingVersion</c> to read.
/// </summary>
public class DeclaredUpdateItemTests : IDisposable
{
    private readonly string _directory;

    public DeclaredUpdateItemTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"CPMigrateUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
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
    public void ScanDeclaredPackages_AnUpdateAmendingAnInclude_IsOneReference()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.*" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.PackageName.Should().Be("Serilog");
        reference
            .Version.Should()
            .Be("4.0.0", "the Update supersedes the version the Include declared");
    }

    [Fact]
    public void ScanDeclaredPackages_AnUpdateAmendsEveryMatchingInclude()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
                <PackageReference Include="Serilog" Version="2.0.0" />
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().OnlyContain(reference => reference.Version == "3.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalUpdateAmendingInclude_RemainsSeparate()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="99.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "4.0.0" && !reference.IsConditional);
        references.Should().Contain(reference => reference.Version == "99.0.0" && reference.IsConditional);
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalMetadataOnlyUpdate_DoesNotChangeIncludePin()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" PrivateAssets="all" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.Version.Should().Be("4.0.0");
        reference.IsConditional.Should().BeFalse();
    }

    [Fact]
    public void ScanDeclaredPackages_StandaloneMetadataOnlyUpdate_IsRetainedForDeclarationRules()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" PrivateAssets="all" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.PackageName.Should().Be("Serilog");
        reference.Version.Should().BeEmpty();
        reference.IsConditional.Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_AnUpdateAddingAVersionOverride_AmendsTheInclude()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
                <PackageReference Update="Serilog" VersionOverride="4.*" />
              </ItemGroup>
            """
        );

        var (references, _) = Scan(path);

        var reference = references.Should().ContainSingle().Subject;
        reference.VersionOverride.Should().Be("4.*");
    }

    [Fact]
    public void ScanDeclaredPackages_VersionedUpdateClearsMetadataOnlyMarker()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" PrivateAssets="all" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.Version.Should().Be("4.0.0");
        reference.IsMetadataOnlyUpdate.Should().BeFalse();
    }

    [Fact]
    public void ScanDeclaredPackages_AnUpdateWithNoIncludeToAmend_StandsOnItsOwn()
    {
        // A project adjusting a reference it inherits. Dropping it would put the VersionOverride
        // beyond reach of every rule again.
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="4.*" />
              </ItemGroup>
            """
        );

        var (references, _) = Scan(path);

        var reference = references.Should().ContainSingle().Subject;
        reference.PackageName.Should().Be("Serilog");
        reference.VersionOverride.Should().Be("4.*");
    }

    [Fact]
    public void ScanDeclaredPackages_TwoIncludesOfOnePackage_StayTwoDeclarations()
    {
        // The duplicate RedundantReference exists to find. Merging by package name would have
        // silenced the rule entirely.
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, _) = Scan(path);

        references.Should().HaveCount(2);
    }

    [Fact]
    public void ScanProjectPackages_UpdateOnlyReference_UsesUpdatePackageName()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.PackageName.Should().Be("Serilog");
        reference.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanProjectPackages_UpdateAmendingInclude_IsOneUpdatedReference()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
                <PackageReference Update="Serilog" Version="2.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.PackageName.Should().Be("Serilog");
        reference.Version.Should().Be("2.0.0");
    }

    [Fact]
    public void ScanProjectPackages_AnUpdateAmendsEveryMatchingInclude()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
                <PackageReference Include="Serilog" Version="2.0.0" />
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().OnlyContain(reference => reference.Version == "3.0.0");
    }

    [Fact]
    public void ScanProjectPackages_ConditionalUpdateAmendingInclude_RemainsSeparate()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="99.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "4.0.0");
        references.Should().Contain(reference => reference.Version == "99.0.0");
    }

    [Fact]
    public void ScanProjectPackages_UnconditionalUpdateAfterConditionalInclude_AmendsOnlyUnconditionalReference()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "3.0.0" && !reference.IsConditional);
        references.Should().Contain(reference => reference.Version == "2.0.0" && reference.IsConditional);
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalUpdateAfterConditionalInclude_RemainsSeparate()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="5.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "4.0.0" && reference.IsConditional);
        references.Should().Contain(reference => reference.Version == "5.0.0" && !reference.IsConditional);
    }

    [Fact]
    public void ScanProjectPackages_UpdateBeforeInclude_IsIgnoredAsInert()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" Version="2.0.0" />
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void ScanProjectPackages_UpdateBeforeConditionalInclude_IsNotInert()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "2.0.0" && !reference.IsConditional);
        references.Should().Contain(reference => reference.Version == "1.0.0" && reference.IsConditional);
    }

    [Fact]
    public void ScanDeclaredPackages_UpdateBeforeInclude_IsIgnoredAsInert()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" Version="2.0.0" />
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_UpdateBeforeConditionalInclude_IsNotInert()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "2.0.0" && !reference.IsConditional);
        references.Should().Contain(reference => reference.Version == "1.0.0" && reference.IsConditional);
    }

    [Fact]
    public void ScanProjectPackages_VariableUpdate_DropsSupersededInclude()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
                <PackageReference Update="Serilog" Version="$(SerilogVersion)" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().BeEmpty();
    }

    private static (List<PackageReference> References, bool Success) Scan(
        string projectPath
    )
    {
        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(projectPath);
        return (references, success);
    }

    private string WriteProject(string itemGroup)
    {
        var path = Path.Combine(_directory, "App.csproj");
        File.WriteAllText(
            path,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            {itemGroup}
            </Project>
            """
        );

        return path;
    }
}
