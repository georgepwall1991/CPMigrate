using System.Reflection;
using CommandLine;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// Short flags are a second spelling of the long options, and the map is hand-maintained — so it
/// drifts: `-v` shipped without an entry (the old hardcoded list simply did not include it), and
/// an explicit `-v` then lost to the config file under CLI-over-config precedence without a word.
/// The reflection pins hold the map to the definitions in both directions so no new flag can
/// slip through the same way.
/// </summary>
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

    [Fact]
    public void GetExplicitArguments_EveryShortFlag_ResolvesToItsLongName()
    {
        // Strictly stronger than the hardcoded list this replaces: a new short flag without a
        // map entry fails here instead of shipping silently.
        var missing = ShortOptions()
            .Where(pair => !CliArgumentParser.GetExplicitArguments(new[] { $"-{pair.Short}" }).Contains(pair.Long))
            .Select(pair => $"-{pair.Short} (expected {pair.Long})")
            .ToList();

        missing.Should().BeEmpty("an explicit short flag must count as explicit");
    }

    [Fact]
    public void GetExplicitArguments_EveryMappedName_IsARealLongOption()
    {
        // The reverse drift: a renamed long option leaving its short mapping pointing at thin air.
        var real = LongOptionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = AllAsciiShorts()
            .SelectMany(shortOpt => CliArgumentParser.GetExplicitArguments(new[] { $"-{shortOpt}" }))
            .Where(name => !real.Contains(name))
            .Distinct()
            .ToList();

        stale.Should().BeEmpty("a short flag must never resolve to an option that no longer exists");
    }

    private static IEnumerable<(string Short, string Long)> ShortOptions()
    {
        return typeof(Options)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<OptionAttribute>())
            .Where(option => option is not null && !string.IsNullOrEmpty(option.ShortName) && !string.IsNullOrEmpty(option.LongName))
            .Select(option => (option!.ShortName, option.LongName));
    }

    private static IEnumerable<string> LongOptionNames()
    {
        return typeof(Options)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<OptionAttribute>())
            .Where(option => option is not null && !string.IsNullOrEmpty(option.LongName))
            .Select(option => option!.LongName);
    }

    private static IEnumerable<char> AllAsciiShorts()
    {
        for (var c = 'a'; c <= 'z'; c++)
        {
            yield return c;
        }
    }
}
