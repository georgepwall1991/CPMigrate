using CPMigrate.Licensing;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Licensing;

public class LicenseScanServiceTests : IDisposable
{
    private readonly string _packagesRoot;

    public LicenseScanServiceTests()
    {
        _packagesRoot = Path.Combine(Path.GetTempPath(), $"cpmigrate-gp-{Guid.NewGuid():N}");
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
    public void Scan_ReadsANuspecOncePerPackageVersion()
    {
        SeedNuspec("mysql.data", "8.0.33", """<package><metadata><license type="expression">GPL-2.0-only</license></metadata></package>""");
        var service = CreateService();
        var first = Reference("MySql.Data", "8.0.33", "/repo/src/Api/Api.csproj");
        var second = Reference("MySql.Data", "8.0.33", "/repo/src/Worker/Worker.csproj");

        var result = service.Scan([first, second], includeTransitive: false);

        result.Failures.Should().Be(0);
        result.Licenses.Should().HaveCount(2);
        result.Licenses.Should().OnlyContain(info => info.Classification == LicenseClassification.StrongCopyleft);
        result.Licenses.Should().OnlyContain(info => info.License == "GPL-2.0-only");
        result.Licenses.Should().OnlyContain(info => info.LicenseType == "expression");
    }

    [Fact]
    public void Scan_MissingNuspec_CountsAFailureAndEmitsNothingForThatPackage()
    {
        var service = CreateService();

        var result = service.Scan(
            [Reference("Ghost.Package", "1.0.0", "/repo/src/Api/Api.csproj")],
            includeTransitive: false
        );

        result.Failures.Should().Be(1);
        result.Licenses.Should().BeEmpty();
    }

    [Fact]
    public void Scan_SkipsTransitiveReferencesUnlessAsked()
    {
        SeedNuspec("itext7", "8.0.0", """<package><metadata><license type="expression">AGPL-3.0-or-later</license></metadata></package>""");
        var direct = Reference("iText7", "8.0.0", "/repo/src/Api/Api.csproj");
        var transitive = Reference("iText7", "8.0.0", "/repo/src/Api/Api.csproj", isTransitive: true);
        var service = CreateService();

        var without = service.Scan([transitive], includeTransitive: false);
        without.Licenses.Should().BeEmpty();
        without.Failures.Should().Be(0);

        var with = service.Scan([direct, transitive], includeTransitive: true);
        with.Licenses.Should().HaveCount(2);
        with.Failures.Should().Be(0);
    }

    [Fact]
    public void Scan_FloatingVersionWithoutAResolvedPin_IsAFailure()
    {
        var service = CreateService();

        var result = service.Scan(
            [Reference("Newtonsoft.Json", "13.0.*", "/repo/src/Api/Api.csproj")],
            includeTransitive: false
        );

        result.Failures.Should().Be(1);
        result.Licenses.Should().BeEmpty();
    }

    [Fact]
    public void Scan_EmptyVersion_IsAFailure()
    {
        var service = CreateService();

        var result = service.Scan(
            [Reference("Newtonsoft.Json", "", "/repo/src/Api/Api.csproj")],
            includeTransitive: false
        );

        result.Failures.Should().Be(1);
        result.Licenses.Should().BeEmpty();
    }

    [Fact]
    public void Scan_UsesVersionOverrideWhenPresent()
    {
        SeedNuspec("serilog", "4.0.0", """<package><metadata><license type="expression">Apache-2.0</license></metadata></package>""");
        var reference = new PackageReference(
            "Serilog",
            "3.0.0",
            "/repo/src/Api/Api.csproj",
            "Api.csproj",
            VersionOverride: "4.0.0"
        );
        var service = CreateService();

        var result = service.Scan([reference], includeTransitive: false);

        result.Failures.Should().Be(0);
        result.Licenses.Should().ContainSingle().Which.Version.Should().Be("4.0.0");
        result.Licenses[0].Classification.Should().Be(LicenseClassification.Permissive);
    }

    [Fact]
    public void Scan_FileLicense_IsUnknown()
    {
        SeedNuspec(
            "microsoft.extensions.logging",
            "8.0.0",
            """<package><metadata><license type="file">LICENSE.txt</license></metadata></package>"""
        );
        var service = CreateService();

        var result = service.Scan(
            [Reference("Microsoft.Extensions.Logging", "8.0.0", "/repo/src/Api/Api.csproj")],
            includeTransitive: false
        );

        result.Failures.Should().Be(0);
        result.Licenses.Should().ContainSingle();
        result.Licenses[0].Classification.Should().Be(LicenseClassification.Unknown);
        result.Licenses[0].LicenseType.Should().Be("file");
    }

    [Fact]
    public void Scan_UnreadableNuspec_IsAFailure()
    {
        SeedNuspec("broken.pkg", "1.0.0", "not xml at all");
        var service = CreateService();

        var result = service.Scan(
            [Reference("Broken.Pkg", "1.0.0", "/repo/src/Api/Api.csproj")],
            includeTransitive: false
        );

        result.Failures.Should().Be(1);
        result.Licenses.Should().BeEmpty();
    }

    [Fact]
    public void Scan_DualLicenseOrMit_IsPermissive()
    {
        SeedNuspec(
            "dual.pkg",
            "1.0.0",
            """<package><metadata><license type="expression">GPL-2.0-only OR MIT</license></metadata></package>"""
        );
        var service = CreateService();

        var result = service.Scan(
            [Reference("Dual.Pkg", "1.0.0", "/repo/src/Api/Api.csproj")],
            includeTransitive: false
        );

        result.Failures.Should().Be(0);
        result.Licenses[0].Classification.Should().Be(LicenseClassification.Permissive);
    }

    private LicenseScanService CreateService()
    {
        return new LicenseScanService(() => _packagesRoot);
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
