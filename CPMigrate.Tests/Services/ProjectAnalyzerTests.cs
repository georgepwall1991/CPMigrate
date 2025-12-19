using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class ProjectAnalyzerTests
{
    private readonly ProjectAnalyzer _analyzer = new(new FakeConsoleService());

    [Fact]
    public void DiscoverProjectsFromSolution_NonExistentDirectory_ReturnsEmpty()
    {
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution("/non/existent/path");

        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectFromPath_NonExistentPath_ReturnsEmpty()
    {
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath("/non/existent/project.csproj");

        projectPaths.Should().BeEmpty();
    }
}
