using CPMigrate;
using FluentAssertions;

namespace CPMigrate.Tests;

public class CliArgumentParserTests
{
    [Fact]
    public void GetExplicitArguments_ReturnsCorrectArguments()
    {
        // Arrange
        var args = new[] { "--solution", "test.sln", "-p", "project.csproj", "--dry-run" };

        // Act
        var result = CliArgumentParser.GetExplicitArguments(args);

        // Assert
        result.Should().Contain("solution");
        result.Should().Contain("project");
        result.Should().Contain("dry-run");
        result.Should().NotContain("interactive");
    }

    [Fact]
    public void GetExplicitArguments_HandlesEqualsSign()
    {
        // Arrange
        var args = new[] { "--solution=test.sln" };

        // Act
        var result = CliArgumentParser.GetExplicitArguments(args);

        // Assert
        result.Should().Contain("solution");
    }

    [Fact]
    public void GetExplicitArguments_HandlesShortOptions()
    {
        // Arrange
        var args = new[] { "-s", "test.sln", "-i", "-q" };

        // Act
        var result = CliArgumentParser.GetExplicitArguments(args);

        // Assert
        result.Should().Contain("solution");
        result.Should().Contain("interactive");
        result.Should().Contain("quiet");
    }

    [Fact]
    public void GetExplicitArguments_HandlesAllShortOptions()
    {
        // Arrange
        var shortOpts = new[] { "s", "p", "o", "k", "n", "d", "r", "a", "i", "q" };
        var expected = new[] { "solution", "project", "output-dir", "keep-attrs", "no-backup", "dry-run", "rollback", "analyze", "interactive", "quiet" };

        foreach (var (shortOpt, longName) in shortOpts.Zip(expected))
        {
            // Act
            var result = CliArgumentParser.GetExplicitArguments(new[] { $"-{shortOpt}" });

            // Assert
            result.Should().Contain(longName);
        }
    }

    [Fact]
    public void GetExplicitArguments_IgnoresInvalidShortOptions()
    {
        // Arrange
        var args = new[] { "-z" };

        // Act
        var result = CliArgumentParser.GetExplicitArguments(args);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetExplicitArguments_IgnoresNonOptions()
    {
        // Arrange
        var args = new[] { "some-value" };

        // Act
        var result = CliArgumentParser.GetExplicitArguments(args);

        // Assert
        result.Should().BeEmpty();
    }
}
