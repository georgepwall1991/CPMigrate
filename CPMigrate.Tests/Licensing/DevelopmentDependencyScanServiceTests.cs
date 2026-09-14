using CPMigrate.Licensing;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Licensing;

/// <summary>
/// The nuspec half of the dev-only signal: <c>developmentDependency="true"</c> is the package's
/// own declaration that it contributes at build time only, read from the global packages folder
/// the same way licenses are.
/// </summary>
public class DevelopmentDependencyScanServiceTests : IDisposable
{
    private readonly string _packagesRoot;

    public DevelopmentDependencyScanServiceTests()
    {
        _packagesRoot = Path.Combine(Path.GetTempPath(), $"cpmigrate-dd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_packagesRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_packagesRoot))
        {
            Directory.Delete(_packagesRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Scan_DevDependencyNuspec_RecordsProjectAndVersion()
    {
        SeedNuspec(
            "contoso.devtool",
            "2.0.0",
            """<package><metadata developmentDependency="true"><id>Contoso.DevTool</id></metadata></package>"""
        );
        var service = CreateService();

        var result = service.Scan([Reference("Contoso.DevTool", "2.0.0", "/repo/src/Api/Api.csproj")]);

        result.Projects.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ProjectDevelopmentDependency("/repo/src/Api/Api.csproj", "Contoso.DevTool"));
        result.Versions.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new PackageVersionKey("Contoso.DevTool", "2.0.0"));
    }

    [Fact]
    public void Scan_NuspecWithoutTheAttribute_RecordsNothing()
    {
        SeedNuspec(
            "serilog",
            "4.0.0",
            """<package><metadata><id>Serilog</id><license type="expression">Apache-2.0</license></metadata></package>"""
        );
        var service = CreateService();

        var result = service.Scan([Reference("Serilog", "4.0.0", "/repo/src/Api/Api.csproj")]);

        result.Projects.Should().BeEmpty();
        result.Versions.Should().BeEmpty();
    }

    [Fact]
    public void Scan_MissingNuspec_IsNotDevOnlyAndNotAFailure()
    {
        // Unrestored packages are a normal state: absence of evidence, never a guess.
        var service = CreateService();

        var result = service.Scan([Reference("Ghost.Package", "1.0.0", "/repo/src/Api/Api.csproj")]);

        result.Projects.Should().BeEmpty();
        result.Versions.Should().BeEmpty();
    }

    [Fact]
    public void Scan_SkipsTransitiveReferences()
    {
        // A transitive package was never declared — nobody's PrivateAssets could scope it anyway.
        SeedNuspec(
            "contoso.devtool",
            "2.0.0",
            """<package><metadata developmentDependency="true"><id>Contoso.DevTool</id></metadata></package>"""
        );
        var service = CreateService();

        var result = service.Scan(
            [Reference("Contoso.DevTool", "2.0.0", "/repo/src/Api/Api.csproj", isTransitive: true)]
        );

        result.Projects.Should().BeEmpty();
        result.Versions.Should().BeEmpty();
    }

    [Fact]
    public void Scan_IsVersionPreciseAcrossProjects()
    {
        // Only 2.0.0 declares itself dev-only; a project resolving 1.0.0 records nothing.
        SeedNuspec(
            "contoso.devtool",
            "2.0.0",
            """<package><metadata developmentDependency="true"><id>Contoso.DevTool</id></metadata></package>"""
        );
        SeedNuspec(
            "contoso.devtool",
            "1.0.0",
            """<package><metadata><id>Contoso.DevTool</id></metadata></package>"""
        );
        var service = CreateService();

        var result = service.Scan(
            [
                Reference("Contoso.DevTool", "2.0.0", "/repo/src/Api/Api.csproj"),
                Reference("Contoso.DevTool", "1.0.0", "/repo/src/Worker/Worker.csproj"),
            ]
        );

        result.Projects.Should()
            .ContainSingle()
            .Which.ProjectPath.Should()
            .Be("/repo/src/Api/Api.csproj");
        result.Versions.Should()
            .ContainSingle()
            .Which.Version.Should()
            .Be("2.0.0");
    }

    [Fact]
    public void Scan_UsesVersionOverrideWhenPresent()
    {
        SeedNuspec(
            "contoso.devtool",
            "2.0.0",
            """<package><metadata developmentDependency="true"><id>Contoso.DevTool</id></metadata></package>"""
        );
        var reference = new PackageReference(
            "Contoso.DevTool",
            "1.0.0",
            "/repo/src/Api/Api.csproj",
            "Api.csproj",
            VersionOverride: "2.0.0"
        );
        var service = CreateService();

        var result = service.Scan([reference]);

        result.Versions.Should().ContainSingle().Which.Version.Should().Be("2.0.0");
    }

    [Fact]
    public void Scan_NormalizesVersionsBeforeLookingThemUp()
    {
        // NuGet writes the version folder normalized: 2.0.0-Beta+build.1 resolves to 2.0.0-beta.
        SeedNuspec(
            "contoso.devtool",
            "2.0.0-beta",
            """<package><metadata developmentDependency="true"><id>Contoso.DevTool</id></metadata></package>"""
        );
        var service = CreateService();

        var result = service.Scan(
            [Reference("Contoso.DevTool", "2.0.0-Beta+build.1", "/repo/src/Api/Api.csproj")]
        );

        result.Versions.Should().ContainSingle();
    }

    [Fact]
    public void IsDevelopmentDependency_UnparseableVersion_IsFalse()
    {
        var service = CreateService();

        service.IsDevelopmentDependency("Contoso.DevTool", "[1.0, )").Should().BeFalse();
    }

    [Fact]
    public void IsDevelopmentDependency_DevDependencyNuspec_IsTrue()
    {
        SeedNuspec(
            "contoso.devtool",
            "2.0.0",
            """<package><metadata developmentDependency="true"><id>Contoso.DevTool</id></metadata></package>"""
        );
        var service = CreateService();

        service.IsDevelopmentDependency("Contoso.DevTool", "2.0.0").Should().BeTrue();
    }

    [Fact]
    public void Scan_UnreadableNuspec_IsNotDevOnly()
    {
        SeedNuspec("broken.pkg", "1.0.0", "not xml at all");
        var service = CreateService();

        var result = service.Scan([Reference("Broken.Pkg", "1.0.0", "/repo/src/Api/Api.csproj")]);

        result.Projects.Should().BeEmpty();
        result.Versions.Should().BeEmpty();
    }

    private DevelopmentDependencyScanService CreateService()
    {
        return new DevelopmentDependencyScanService(() => _packagesRoot);
    }

    private void SeedNuspec(string packageIdLower, string version, string xml)
    {
        var directory = Path.Combine(_packagesRoot, packageIdLower, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{packageIdLower}.nuspec"), xml);
    }

    private static PackageReference Reference(
        string name,
        string version,
        string projectPath,
        bool isTransitive = false
    )
    {
        return new PackageReference(
            name,
            version,
            projectPath,
            Path.GetFileName(projectPath),
            IsTransitive: isTransitive
        );
    }
}
