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
    public void ScanDeclaredPackages_UnconditionalUpdateAfterConditionalUpdate_ClearsConditionality()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="4.0.0" />
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
    public void ScanDeclaredPackages_UnconditionalVersionUpdatePreservesConditionalVersionOverrideAndAddsBaseRecord()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="9.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            reference.Version == "3.0.0"
            && reference.VersionOverride == null
            && !reference.IsConditional
        );
        references.Should().Contain(reference =>
            reference.Version == "3.0.0"
            && reference.VersionOverride == "9.0.0"
            && reference.IsConditional
        );
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalVersionUpdateKeepsConditionalOverrideAlongsideInclude()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="9.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            reference.Version == "3.0.0" && !reference.IsConditional
        );
        references.Should().Contain(reference =>
            reference.VersionOverride == "9.0.0" && reference.IsConditional
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalVersionUpdatePreservesInheritedVersionOverride()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="4.*" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            reference.VersionOverride == "4.0.0" && !reference.IsConditional
        );
        references.Should().Contain(reference =>
            reference.Version == "4.*"
            && reference.VersionOverride == "4.0.0"
            && reference.IsConditional
        );
    }

    [Fact]
    public void ScanDeclaredPackages_WiderConditionAmendsItemWithAdditionalAncestorCondition()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference
                    Include="Serilog"
                    Condition="'$(Configuration)' == 'Debug'"
                    Version="4.*" />
              </ItemGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalVersionOverrideAfterConditionalIncludeRemainsConditional()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="5.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle(reference =>
            reference.IsConditional && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ChooseConditionMatchesEquivalentExternalUpdate()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <ItemGroup>
                <PackageReference
                    Update="Serilog"
                    Condition="'$(Configuration)' == 'Debug'"
                    Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_SiblingChooseBranchesWithPathlessNestedConditionsRemainSeparate()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference
                        Include="Serilog"
                        Condition="'$(TargetFramework)' == 'net8.0'"
                        Version="4.*" />
                  </ItemGroup>
                </When>
                <When Condition="'$(TargetFramework)' == 'net8.0'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "4.*");
        references.Should().Contain(reference => reference.Version == "4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentBranchesInIndependentChooseElementsAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentIndependentChooseGuardsWithDifferentSpacingAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(A)' == '1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'$(A)'=='1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentIndependentChooseNotEqualGuardsWithDifferentSpacingAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(A)' != '1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'$(A)'!='1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentIndependentChooseParenthesizedGuardsWithDifferentSpacingAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="('$(A)' == '1')">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="( '$(A)'=='1' )">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentIndependentChooseCaseInsensitiveEqualityGuardsAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(A)' == 'DEBUG'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'$(A)' == 'debug'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentIndependentChooseLeftLiteralEqualityGuardsAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'DEBUG' == '$(A)'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'debug' == '$(A)'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_IndependentChooseBranchesWithDifferentPrecedingGuardsRemainSeparate()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'$(Configuration)' == 'CI'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "4.*");
        references.Should().Contain(reference => reference.Version == "4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_IndependentChooseBranchesWithEquivalentGuardSetsAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'">
                  <ItemGroup />
                </When>
                <When Condition="'$(TargetFramework)' == 'net9.0'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'$(TargetFramework)' == 'net9.0'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Release'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_NoOpConditionalEmptyVersionDoesNotProtectCentralVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(2);
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeFalse();
    }

    [Fact]
    public void ScanDeclaredPackages_EmptyVersionOnlyClearsAnOverlappingInlineVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(3);
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_GuardSetBoundariesRemainDistinctWhenGuardsContainAnd()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(A)' == '1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(B)' == '1' &amp;&amp; '$(C)' == '1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <Choose>
                <When Condition="'$(A)' == '1' &amp;&amp; '$(B)' == '1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(C)' == '1'">
                  <ItemGroup />
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "4.*");
        references.Should().Contain(reference => reference.Version == "4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalClearWithPropertyValuedGuardCanOverlap()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == '$(CurrentTargetFramework)'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(3);
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalClearWithExpandableGuardCanOverlap()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == '@(CurrentTargetFramework)'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(3);
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_LiteralFirstConditionsRemainPotentiallyOverlapping()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'net8.0' == '$(TargetFramework)'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'net9.0' == '$(TargetFramework)'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_ParenthesizedReassignedTargetFrameworkConditionsRemainOverlapping()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="('$(TargetFramework)' == 'net8.0')">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup Condition="('$(TargetFramework)' == 'net9.0')">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_MutablePropertyConditionsRemainPotentiallyOverlapping()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'$(Mode)' == 'A'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <Mode>B</Mode>
              </PropertyGroup>
              <ItemGroup Condition="'$(Mode)' == 'B'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_ReassignedConfigurationRemainsPotentiallyOverlapping()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <Configuration>Release</Configuration>
              </PropertyGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Release'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_ReassignedTargetFrameworkPropertyRemainsPotentiallyOverlapping()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <PropertyGroup>
                <TargetFrameworkIdentifier>.NETStandard</TargetFrameworkIdentifier>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETStandard'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_MultiItemUpdateAmendsEachPackage()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Foo" Version="1.*" />
                <PackageReference Include="Bar" Version="2.*" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Foo;Bar" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().OnlyContain(reference => reference.Version == "3.0.0");
        references.Should().Contain(reference => reference.PackageName == "Foo");
        references.Should().Contain(reference => reference.PackageName == "Bar");
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionedVersionMetadataRemainsConditional()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">1.0.0</Version>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.PackageName == "Serilog"
            && reference.Version == "1.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionedOverridePreservesUnconditionalVersionProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" Version="1.0.0">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">2.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            !reference.IsConditional
            && reference.PackageName == "Serilog"
            && reference.Version == "1.0.0"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.PackageName == "Serilog"
            && reference.Version == "1.0.0"
            && reference.VersionOverride == "2.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_IndependentlyConditionedMetadataUsesSeparateProjections()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net9.0'">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == string.Empty
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_IndependentlyConditionedMetadataUnderItemConditionSplits()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net9.0'">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == string.Empty
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentConditionedMetadataUsesOneProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'NET8.0'">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_OverlappingConditionedMetadataPreservesOverridePrecedence()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0' And '$(Configuration)' == 'Debug'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionedMetadataWithPropertyNameCasingUsesOneProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="'$(targetframework)' == 'net8.0'">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_WiderVersionGuardPreservesVersionOutsideNarrowerOverride()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0' And '$(Configuration)' == 'Debug'">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == string.Empty
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_GroupedNarrowerOverrideGuardPreservesWiderVersionBranch()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="('$(TargetFramework)' == 'net8.0' And '$(Configuration)' == 'Debug')">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == string.Empty
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionedMetadataOnUnconditionalIncludePreservesBaseProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">2.0.0</Version>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            !reference.IsConditional
            && reference.Version == string.Empty
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "2.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_DuplicateMetadataConditionsFoldSameGuardUpdate()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">5.0.0</VersionOverride>
                </PackageReference>
                <PackageReference Update="Serilog">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">6.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == "6.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_EachUnconditionalIncludeWithConditionedMetadataKeepsItsBaseProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0'">2.0.0</Version>
                </PackageReference>
                <PackageReference Include="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net9.0'">3.0.0</Version>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Count(reference =>
                !reference.IsConditional
                && reference.Version == string.Empty
                && reference.VersionOverride == null
            )
            .Should()
            .Be(2);
    }

    [Fact]
    public void ScanDeclaredPackages_UnsupportedWiderVersionGuardPreservesPotentialVersionBranch()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0' Or '$(TargetFramework)' == 'net9.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">5.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == string.Empty
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_UnsupportedWiderOverrideUpdateAmendsNarrowerProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0' Or '$(TargetFramework)' == 'net9.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">5.*</VersionOverride>
                </PackageReference>
                <PackageReference Update="Serilog">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0' Or '$(TargetFramework)' == 'net9.0'">6.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().NotContain(reference => reference.VersionOverride == "5.*");
        references.Should().Contain(reference => reference.VersionOverride == "6.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_SplitOverrideClearPreservesCoveredVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" VersionOverride="5.0.0" />
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net8.0' Or '$(TargetFramework)' == 'net9.0'">4.*</Version>
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'"></VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "4.*"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_GroupedNarrowerProjectionAmendsWithWiderDisjunctiveUpdate()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <Version Condition="'$(TargetFramework)' == 'net9.0' Or '$(TargetFramework)' == 'net10.0'">4.*</Version>
                  <VersionOverride Condition="('$(TargetFramework)' == 'net9.0')">5.*</VersionOverride>
                </PackageReference>
                <PackageReference Update="Serilog">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net9.0' Or '$(TargetFramework)' == 'net10.0'">6.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().NotContain(reference => reference.VersionOverride == "5.*");
        references.Should().Contain(reference => reference.VersionOverride == "6.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_WiderConjunctiveUpdateAmendsNarrowerConjunctiveProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0' And '$(Configuration)' == 'Debug'">5.*</VersionOverride>
                </PackageReference>
                <PackageReference Update="Serilog">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0'">6.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().NotContain(reference => reference.VersionOverride == "5.*");
        references.Should().Contain(reference => reference.VersionOverride == "6.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentDisjunctionUpdateAmendsPreviousProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net8.0' Or '$(TargetFramework)' == 'net9.0'">5.*</VersionOverride>
                </PackageReference>
                <PackageReference Update="Serilog">
                  <VersionOverride Condition="'$(TargetFramework)' == 'net9.0' Or '$(TargetFramework)' == 'net8.0'">6.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        references.Should().NotContain(reference => reference.VersionOverride == "5.*");
        references.Should().Contain(reference => reference.VersionOverride == "6.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentOtherwiseBranchesInIndependentChooseElementsAmend()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'">
                  <ItemGroup />
                </When>
                <Otherwise>
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'">
                  <ItemGroup />
                </When>
                <Otherwise>
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalVersionSurvivesUnconditionalOverrideClear()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            !reference.IsConditional
            && reference.Version == "1.0.0"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalVersionSurvivesTemporaryOverrideBeforeClear()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            !reference.IsConditional
            && reference.Version == "1.0.0"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalVersionRemainsUnconditionalAfterTemporaryOverride()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle();
        references.Should().ContainSingle(reference =>
            !reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalVersionResetsConditionalVersionProvenance()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="2.0.0" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle(reference =>
            !reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalIncludeDoesNotCreateUnconditionalProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Serilog" Version="4.*" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle();
        references.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "4.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalVersionOnlyUpdateKeepsLatestSameScopeVersionOverride()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="2.*" />
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            reference.VersionOverride == "1.0.0" && !reference.IsConditional
        );
        references.Should().Contain(reference =>
            reference.Version == "3.0.0"
            && reference.VersionOverride == "2.*"
            && reference.IsConditional
        );
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalVersionOverrideAfterConditionalVersionUpdateRetainsBothEffectiveProjections()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Update="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="5.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            !reference.IsConditional
            && reference.VersionOverride == "5.0.0"
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "2.0.0"
            && reference.VersionOverride == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalVersionOverrideClearAfterConditionalVersionUpdateRetainsBaseProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Update="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(2);
        declaredReferences.Should().ContainSingle(reference => !reference.IsConditional);
        declaredReferences.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "2.0.0"
            && reference.VersionOverride == null
        );

        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "1.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "1.0.0").Should().BeFalse();
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalVersionOverrideClearAfterConditionalVersionAndOverrideClearRetainsBaseProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Update="Serilog" Version="3.0.0" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(2);
        declaredReferences.Should().ContainSingle(reference => !reference.IsConditional);
        declaredReferences.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );

        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "1.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "1.0.0").Should().BeFalse();
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalEmptyVersionAndOverrideClearDoesNotCreateBaseProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Update="Serilog" Version="" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == ""
            && reference.VersionOverride == null
        );

        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "1.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "1.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_EquivalentConditionSyntaxFoldsConditionalUpdate()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(TargetFramework)' == 'net10.0'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.*" />
                  </ItemGroup>
                </When>
              </Choose>
              <ItemGroup>
                <PackageReference
                    Update="Serilog"
                    Condition="'$(TargetFramework)'=='net10.0'"
                    Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "4.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_NarrowerConditionalVersionUpdateInheritsWiderScopeVersionOverride()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
                <PackageReference
                    Update="Serilog"
                    Condition="'$(Configuration)' == 'Debug'"
                    Version="4.*" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            reference.VersionOverride == "4.0.0"
            && reference.IsConditional
            && reference.Version == "4.*"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_WiderConditionalVersionOverrideAmendsNarrowerProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="4.*" />
                <PackageReference
                    Update="Serilog"
                    Condition="'$(Configuration)' == 'Debug'"
                    Version="4.*" />
                <PackageReference Update="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().OnlyContain(reference => reference.VersionOverride == "4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_ExplicitEmptyVersionOverrideClearsPriorOverride()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="2.*" />
                <PackageReference Update="Serilog" Version="3.0.0" VersionOverride="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.Version.Should().Be("3.0.0");
        reference.VersionOverride.Should().BeNull();
    }

    [Fact]
    public void ScanDeclaredPackages_ExplicitEmptyVersionOverrideOnlyClearsPriorOverride()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" VersionOverride="2.*" />
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.VersionOverride.Should().BeNull();
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalOverrideClearPreservesInheritedVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="3.0.0" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalOverrideClearSurvivesLaterUnconditionalVersionUpdate()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" VersionOverride="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference =>
            !reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == "2.0.0"
        );
        references.Should().Contain(reference =>
            reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_InheritedConditionalOverrideClearDoesNotCreateUnconditionalProjection()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(1);
        references.Should().ContainSingle(reference =>
            reference.IsConditional
            && reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
    }

    [Fact]
    public void ScanDeclaredPackages_InheritedClearBranchSuppressesProjectionAlongsideOverrideBranch()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" VersionOverride="" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
                <PackageReference Update="Serilog" VersionOverride="9.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().OnlyContain(reference => reference.IsConditional);
        references.Should().Contain(reference =>
            reference.Version == "3.0.0"
            && reference.VersionOverride == null
        );
        references.Should().Contain(reference =>
            reference.Version == "3.0.0"
            && reference.VersionOverride == "9.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalEmptyVersionClearProtectsResolvedCentralVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (declaredReferences, success) = scanner.ScanDeclaredPackages(path);

        success.Should().BeTrue();
        declaredReferences.Should().HaveCount(2);
        var packageInfo = new ProjectPackageInfo(
            new[] { new PackageReference("Serilog", "2.0.0", path, "App.csproj") },
            DeclaredReferences: declaredReferences
        );

        packageInfo.IsConditionallyDeclared(path, "Serilog", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalUpdateAfterConditionalIncludeRemainsConditional()
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
        references.Should().ContainSingle(reference =>
            reference.IsConditional && reference.Version == "5.0.0"
        );
    }

    [Fact]
    public void ScanDeclaredPackages_ExplicitEmptyVersionClearsPriorVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Update="Serilog" Version="2.*" />
                <PackageReference Update="Serilog" Version="" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().BeEmpty();
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalUpdateFoldsOverriddenConditionalUpdate()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("3.0.0");
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
    public void ScanProjectPackages_ConditionalUpdatesUnderSameCondition_FoldSupersededVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="4.*" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanProjectPackages_ConditionalIncludeAndUpdateUnderSameCondition_FoldSupersededVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.*" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanProjectPackages_ConditionalUpdatesUnderDifferentConditions_RemainSeparate()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
                <PackageReference Update="Serilog" Version="5.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "4.0.0");
        references.Should().Contain(reference => reference.Version == "5.0.0");
    }

    [Fact]
    public void ScanProjectPackages_IndependentChooseOtherwiseBranches_RemainSeparate()
    {
        var path = WriteProject(
            """
              <Choose>
                <When Condition="'$(TargetFramework)' == 'net8.0'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
                <Otherwise>
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.*" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
              <Choose>
                <When Condition="'$(TargetFramework)' == 'net9.0'">
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="5.0.0" />
                  </ItemGroup>
                </When>
                <Otherwise>
                  <ItemGroup>
                    <PackageReference Update="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().HaveCount(4);
        references.Should().Contain(reference => reference.Version == "4.*");
        references.Should().Contain(reference => reference.Version == "4.0.0");
        references.Should().Contain(reference => reference.Version == "5.0.0");
    }

    [Fact]
    public void ScanProjectPackages_UnconditionalUpdateAfterConditionalUpdate_ClearsConditionality()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        var reference = references.Should().ContainSingle().Subject;
        reference.Version.Should().Be("4.0.0");
        reference.IsConditional.Should().BeFalse();
    }

    [Fact]
    public void ScanProjectPackages_UnconditionalUpdateFoldsOverriddenConditionalUpdate()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            """
        );

        var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
        var (references, success) = scanner.ScanProjectPackages(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("3.0.0");
    }

    [Fact]
    public void ScanProjectPackages_UnconditionalUpdateAfterConditionalInclude_AmendsEveryEarlierReference()
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
        references.Should().Contain(reference => reference.Version == "3.0.0" && reference.IsConditional);
    }

    [Fact]
    public void ScanDeclaredPackages_UnconditionalUpdateAfterConditionalInclude_RemainsConditional()
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
        references.Should().ContainSingle(reference =>
            reference.Version == "5.0.0" && reference.IsConditional
        );
    }

    [Fact]
    public void ScanProjectPackages_UpdateBeforeInclude_RetainsPotentialInheritedUpdate()
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
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "2.0.0");
        references.Should().Contain(reference => reference.Version == "1.0.0");
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
    public void ScanDeclaredPackages_UpdateBeforeInclude_RetainsPotentialInheritedUpdate()
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
        references.Should().HaveCount(2);
        references.Should().Contain(reference => reference.Version == "2.0.0");
        references.Should().Contain(reference => reference.Version == "1.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalUpdatesUnderSameCondition_FoldSupersededVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Update="Serilog" Version="4.*" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void ScanDeclaredPackages_ConditionalIncludeAndUpdateUnderSameCondition_FoldSupersededVersion()
    {
        var path = WriteProject(
            """
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.*" />
                <PackageReference Update="Serilog" Version="4.0.0" />
              </ItemGroup>
            """
        );

        var (references, success) = Scan(path);

        success.Should().BeTrue();
        references.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
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

    [Fact]
    public void ScanProjectPackages_ExpandableUpdate_DropsSupersededInclude()
    {
        var path = WriteProject(
            """
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
                <PackageReference Update="Serilog" Version="@(SelectedVersion)" />
                <PackageReference Update="Serilog" Version="%(Versions.Identity)" />
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
