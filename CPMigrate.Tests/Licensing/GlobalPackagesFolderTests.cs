using CPMigrate.Licensing;
using FluentAssertions;

namespace CPMigrate.Tests.Licensing;

public class GlobalPackagesFolderTests
{
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
}
