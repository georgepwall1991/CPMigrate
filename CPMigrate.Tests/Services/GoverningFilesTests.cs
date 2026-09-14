using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class GoverningFilesTests : IDisposable
{
    private readonly string _directory;

    public GoverningFilesTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"CPMigrateGov_{Guid.NewGuid():N}");
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
    public void FindNearestPropsFile_PropsInStartDirectory_ReturnsIt()
    {
        var propsPath = Path.Combine(_directory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project />");

        GoverningFiles.FindNearestPropsFile(_directory).Should().Be(propsPath);
    }

    [Fact]
    public void FindNearestPropsFile_PropsInAncestor_WalksUp()
    {
        var propsPath = Path.Combine(_directory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project />");
        var nested = Path.Combine(_directory, "src", "app");
        Directory.CreateDirectory(nested);

        GoverningFiles.FindNearestPropsFile(nested).Should().Be(propsPath);
    }

    [Fact]
    public void FindNearestPropsFile_PropsAtTwoLevels_NearestWins()
    {
        File.WriteAllText(Path.Combine(_directory, "Directory.Packages.props"), "<Project />");
        var nested = Path.Combine(_directory, "src");
        Directory.CreateDirectory(nested);
        var nearer = Path.Combine(nested, "Directory.Packages.props");
        File.WriteAllText(nearer, "<Project />");

        GoverningFiles.FindNearestPropsFile(nested).Should().Be(nearer);
    }

    [Fact]
    public void FindNearestPropsFile_NoPropsAnywhere_ReturnsNull()
    {
        GoverningFiles.FindNearestPropsFile(_directory).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FindNearestPropsFile_EmptyStart_ReturnsNull(string? start)
    {
        // An empty path would anchor the walk at the process working directory and could open a
        // props file that has nothing to do with the target the caller named.
        GoverningFiles.FindNearestPropsFile(start).Should().BeNull();
    }

    [Fact]
    public void FindNearestPropsFile_NonexistentStart_ReturnsNull()
    {
        // Walking up from a path that names nothing resolves a props file governing a tree that
        // isn't there — the current directory's ancestors are not the target's ancestors.
        var missing = Path.Combine(_directory, "does-not-exist");

        GoverningFiles.FindNearestPropsFile(missing).Should().BeNull();
    }

    [Fact]
    public void FindSolutionFile_SlnPresent_ReturnsIt()
    {
        var sln = Path.Combine(_directory, "App.sln");
        File.WriteAllText(sln, "");

        GoverningFiles.FindSolutionFile(_directory).Should().Be(sln);
    }

    [Fact]
    public void FindSolutionFile_SlnxPresent_ReturnsIt()
    {
        var slnx = Path.Combine(_directory, "App.slnx");
        File.WriteAllText(slnx, "<Solution />");

        GoverningFiles.FindSolutionFile(_directory).Should().Be(slnx);
    }

    [Fact]
    public void FindSolutionFile_MultipleSolutions_PicksDeterministically()
    {
        var first = Path.Combine(_directory, "A.slnx");
        File.WriteAllText(Path.Combine(_directory, "Z.sln"), "");
        File.WriteAllText(first, "<Solution />");

        GoverningFiles.FindSolutionFile(_directory).Should().Be(first);
    }

    [Fact]
    public void FindSolutionFile_LookalikeExtension_NotMatched()
    {
        File.WriteAllText(Path.Combine(_directory, "App.sln.bak"), "");

        GoverningFiles.FindSolutionFile(_directory).Should().BeNull();
    }

    [Fact]
    public void FindSolutionFile_NonexistentDirectory_ReturnsNull()
    {
        GoverningFiles.FindSolutionFile(Path.Combine(_directory, "missing")).Should().BeNull();
    }
}
