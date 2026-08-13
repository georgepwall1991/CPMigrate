using CPMigrate.Licensing;
using FluentAssertions;

namespace CPMigrate.Tests.Licensing;

/// <summary>
/// Nuspec license metadata is XML, often namespaced, and comes in three historical shapes.
/// These snippets follow the real NuGet layouts rather than a simplified schema.
/// </summary>
public class NuspecLicenseReaderTests
{
    [Fact]
    public void TryRead_ExpressionLicense_ReturnsTheSpdxText()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Newtonsoft.Json</id>
                <version>13.0.3</version>
                <license type="expression">MIT</license>
              </metadata>
            </package>
            """;

        NuspecLicenseReader.TryRead(xml, out var license).Should().BeTrue();
        license.Should().BeEquivalentTo(new NuspecLicense("MIT", "expression", LicenseUrl: null));
    }

    [Fact]
    public void TryRead_FileLicense_DoesNotFetchTheFile()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
              <metadata>
                <id>Microsoft.Extensions.Logging</id>
                <license type="file">LICENSE.txt</license>
              </metadata>
            </package>
            """;

        NuspecLicenseReader.TryRead(xml, out var license).Should().BeTrue();
        license.Should().BeEquivalentTo(new NuspecLicense("LICENSE.txt", "file", LicenseUrl: null));
    }

    [Fact]
    public void TryRead_LegacyLicenseUrl_RecordsTheUrlWithoutFetchingIt()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
              <metadata>
                <id>OldPackage</id>
                <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
              </metadata>
            </package>
            """;

        NuspecLicenseReader.TryRead(xml, out var license).Should().BeTrue();
        license
            .Should()
            .BeEquivalentTo(new NuspecLicense(null, "url", "https://licenses.nuget.org/MIT"));
    }

    [Fact]
    public void TryRead_MissingLicense_IsMissingNotAParseFailure()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <package>
              <metadata>
                <id>Unlicensed.Package</id>
                <version>1.0.0</version>
              </metadata>
            </package>
            """;

        NuspecLicenseReader.TryRead(xml, out var license).Should().BeTrue();
        license.Should().BeEquivalentTo(new NuspecLicense(null, "missing", LicenseUrl: null));
    }

    [Fact]
    public void TryRead_DualLicenseExpression_KeepsTheWholeExpression()
    {
        const string xml = """
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <license type="expression">GPL-2.0-only OR MIT</license>
              </metadata>
            </package>
            """;

        NuspecLicenseReader.TryRead(xml, out var license).Should().BeTrue();
        license!.Expression.Should().Be("GPL-2.0-only OR MIT");
        license.LicenseType.Should().Be("expression");
    }

    [Fact]
    public void TryRead_AgplExpression_IsReadAsExpression()
    {
        const string xml = """
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>itext7</id>
                <license type="expression">AGPL-3.0-or-later</license>
              </metadata>
            </package>
            """;

        NuspecLicenseReader.TryRead(xml, out var license).Should().BeTrue();
        license!.Expression.Should().Be("AGPL-3.0-or-later");
    }

    [Fact]
    public void TryRead_ExpressionTakesPrecedenceOverLicenseUrl()
    {
        const string xml = """
            <package>
              <metadata>
                <license type="expression">Apache-2.0</license>
                <licenseUrl>https://www.apache.org/licenses/LICENSE-2.0</licenseUrl>
              </metadata>
            </package>
            """;

        NuspecLicenseReader.TryRead(xml, out var license).Should().BeTrue();
        license.Should().BeEquivalentTo(new NuspecLicense("Apache-2.0", "expression", LicenseUrl: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<package>")]
    public void TryRead_MalformedXml_ReturnsFalse(string xml)
    {
        NuspecLicenseReader.TryRead(xml, out var license).Should().BeFalse();
        license.Should().BeNull();
    }

    [Fact]
    public void TryReadFile_ReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpmigrate-nuspec-{Guid.NewGuid():N}.nuspec");
        File.WriteAllText(
            path,
            """
            <package>
              <metadata>
                <license type="expression">BSD-3-Clause</license>
              </metadata>
            </package>
            """
        );

        try
        {
            NuspecLicenseReader.TryReadFile(path, out var license).Should().BeTrue();
            license!.Expression.Should().Be("BSD-3-Clause");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadFile_MissingPath_ReturnsFalse()
    {
        NuspecLicenseReader.TryReadFile("/no/such/package.nuspec", out var license).Should().BeFalse();
        license.Should().BeNull();
    }
}
