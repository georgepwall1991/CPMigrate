using CPMigrate.Analyzers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

/// <summary>
/// Where the central pins actually are.
///
/// <para>
/// Every rule about central package management reads them, so a lookup that misses the file reports
/// the same thing as a solution with nothing wrong — and one that finds it but reads it as switched
/// off reports a High-severity finding about a perfectly good repository. Both were reachable
/// through ordinary MSBuild layouts that this had not modelled: a props file above the scan root,
/// and enablement set in a file the props file imports.
/// </para>
/// </summary>
public class CentralPinDiscoveryTests : IDisposable
{
    private readonly string _root;

    public CentralPinDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigratePins_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadEffectiveCentralVersions_PropsInTheScanRoot_IsFound()
    {
        Write("Directory.Packages.props", Props(("Serilog", "4.0.0")));

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(_root).Should().ContainKey("Serilog");
    }

    [Fact]
    public void ReadEffectiveCentralVersions_PropsAboveTheScanRoot_IsFound()
    {
        // NuGet resolves Directory.Packages.props by walking up, so pointing --analyze at one
        // solution inside a repository still has the repository's pins in effect. Looking only at
        // the scan root reported that solution as having no central versions at all.
        Write("Directory.Packages.props", Props(("Serilog", "4.0.0")));
        var nested = Path.Combine(_root, "src", "Api");
        Directory.CreateDirectory(nested);

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(nested).Should().ContainKey("Serilog");
    }

    [Fact]
    public void ReadEffectiveCentralVersions_NearestPropsWins()
    {
        // MSBuild stops at the first one it finds, so a nested props file is the effective one.
        Write("Directory.Packages.props", Props(("Serilog", "4.0.0")));
        var nested = Path.Combine(_root, "src");
        Directory.CreateDirectory(nested);
        Write(Path.Combine("src", "Directory.Packages.props"), Props(("Serilog", "3.0.0")));

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(nested)["Serilog"].Should().Be("3.0.0");
    }

    [Fact]
    public void ReadEffectiveCentralVersions_DoesNotClimbOutOfTheRepository()
    {
        // A props file in a parent of the checkout belongs to something else. Walking past the
        // repository root would let an unrelated file on the machine decide what a scan reports,
        // which is both surprising and unreproducible on another machine.
        Write("Directory.Packages.props", Props(("Serilog", "4.0.0")));
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(repository).Should().BeEmpty();
    }

    [Fact]
    public void ReadEffectiveCentralVersions_EnablementInAnImportedFile_CountsAsEnabled()
    {
        // MSBuild resolves the property through imports. Reading only the props file itself and a
        // sibling Directory.Build.props called this repository unmanaged and returned no pins.
        Write(
            "Enable.props",
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        Write(
            "Directory.Packages.props",
            """
            <Project>
              <Import Project="Enable.props" />
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(_root).Should().ContainKey("Serilog");
    }

    [Fact]
    public void ReadEffectiveCentralVersions_EnablementSwitchedOffInAnImport_StaysOff()
    {
        // The reverse has to hold too, or an import would only ever be able to turn CPM on.
        Write(
            "Enable.props",
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        Write(
            "Directory.Packages.props",
            """
            <Project>
              <Import Project="Enable.props" />
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(_root).Should().BeEmpty();
    }

    [Fact]
    public void ReadEffectiveCentralVersions_AWorktreeRootIsStillABoundary()
    {
        // .git is a directory in an ordinary clone but a file in a linked worktree or submodule.
        // Testing only for the directory walked straight past those roots.
        Write("Directory.Packages.props", Props(("Serilog", "4.0.0")));
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /somewhere/else");

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(worktree).Should().BeEmpty();
    }

    [Fact]
    public void ReadEffectiveCentralVersions_EnablementInADirectoryBuildPropsBesideTheProps_CountsAsEnabled()
    {
        // Once the props file can come from an ancestor, looking for Directory.Build.props only
        // beside the scan root misses the one sitting next to the props file — and reports
        // CpmNotEnabled, a High finding, on a repository that is correctly set up.
        Write(
            "Directory.Build.props",
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        Write(
            "Directory.Packages.props",
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var nested = Path.Combine(_root, "src", "Api");
        Directory.CreateDirectory(nested);

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(nested).Should().ContainKey("Serilog");
    }

    [Fact]
    public void ReadEffectiveCentralVersions_APropertySetAfterAnImport_Wins()
    {
        // MSBuild evaluates in document order and the last assignment wins. Reading the local value
        // first would have let an import turn central management on but never off — here the local
        // 'false' comes after the import and has to win.
        Write(
            "Enable.props",
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        Write(
            "Directory.Packages.props",
            """
            <Project>
              <Import Project="Enable.props" />
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(_root).Should().BeEmpty();
    }

    [Fact]
    public void ReadEffectiveCentralVersions_AConditionalImport_DoesNotDecideEnablement()
    {
        // Whether it applies depends on properties this cannot evaluate. Following it could switch
        // central management on where NuGet leaves it off, and the drift rules would then judge
        // every project against pins that are never applied.
        Write(
            "Enable.props",
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """
        );
        Write(
            "Directory.Packages.props",
            """
            <Project>
              <Import Project="Enable.props" Condition="'$(Configuration)' == 'Release'" />
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        CpmDriftAnalyzer.ReadEffectiveCentralVersions(_root).Should().BeEmpty();
    }

    [Fact]
    public void ReadEffectiveCentralVersions_NoPropsAnywhere_IsEmpty()
    {
        CpmDriftAnalyzer.ReadEffectiveCentralVersions(_root).Should().BeEmpty();
    }

    private static string Props(params (string Package, string Version)[] pins)
    {
        var items = string.Join(
            "\n    ",
            pins.Select(pin =>
                $"""<PackageVersion Include="{pin.Package}" Version="{pin.Version}" />"""
            )
        );

        return $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                {items}
              </ItemGroup>
            </Project>
            """;
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
