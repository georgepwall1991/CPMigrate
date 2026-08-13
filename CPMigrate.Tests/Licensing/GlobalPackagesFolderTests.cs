using CPMigrate.Licensing;
using FluentAssertions;

namespace CPMigrate.Tests.Licensing;

public class GlobalPackagesFolderTests
{
    [Fact]
    public void Resolve_ExplicitEnvironmentValueWinsOverTheProcessVariable()
    {
        var previous = Environment.GetEnvironmentVariable(GlobalPackagesFolder.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(GlobalPackagesFolder.EnvironmentVariableName, "/from-process");

            GlobalPackagesFolder
                .Resolve(environmentValue: "/from-argument", userProfile: "/home/dev")
                .Should()
                .Be(Path.GetFullPath("/from-argument"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GlobalPackagesFolder.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void Resolve_PrefersTheNuGetPackagesEnvironmentVariable()
    {
        GlobalPackagesFolder
            .Resolve(environmentValue: "/custom/packages", userProfile: "/home/dev")
            .Should()
            .Be(Path.GetFullPath("/custom/packages"));
    }

    [Fact]
    public void Resolve_FallsBackToTheUserProfilePackagesFolder()
    {
        var expected = Path.GetFullPath(Path.Combine("/home/dev", ".nuget", "packages"));

        GlobalPackagesFolder
            .Resolve(environmentValue: null, userProfile: "/home/dev")
            .Should()
            .Be(expected);
    }

    [Fact]
    public void Resolve_IgnoresABlankEnvironmentVariable()
    {
        var expected = Path.GetFullPath(Path.Combine("/home/dev", ".nuget", "packages"));

        GlobalPackagesFolder
            .Resolve(environmentValue: "  ", userProfile: "/home/dev")
            .Should()
            .Be(expected);
    }

    [Fact]
    public void NuspecPath_LowercasesThePackageIdTheWayNuGetDoes()
    {
        var path = GlobalPackagesFolder.NuspecPath("/packages", "Newtonsoft.Json", "13.0.3");

        path.Should()
            .Be(
                Path.GetFullPath(
                    Path.Combine("/packages", "newtonsoft.json", "13.0.3", "newtonsoft.json.nuspec")
                )
            );
    }

    [Fact]
    public void NuspecPath_NormalizesAndLowercasesTheVersionFolderTheWayNuGetDoes()
    {
        var path = GlobalPackagesFolder.NuspecPath("/packages", "Foo.Bar", "1.0.0-Beta+build.1");

        path.Should()
            .Be(
                Path.GetFullPath(
                    Path.Combine("/packages", "foo.bar", "1.0.0-beta", "foo.bar.nuspec")
                )
            );
    }

    [Fact]
    public void NuspecPath_ExpandsATwoPartVersionToThreeDigits()
    {
        var path = GlobalPackagesFolder.NuspecPath("/packages", "Foo", "1.0");

        path.Should()
            .Be(Path.GetFullPath(Path.Combine("/packages", "foo", "1.0.0", "foo.nuspec")));
    }
}
