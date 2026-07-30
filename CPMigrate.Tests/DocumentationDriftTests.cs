using System.Reflection;
using System.Text.RegularExpressions;
using CommandLine;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// Holds the documentation to the tool, so it cannot drift quietly.
///
/// A flag that exists and is undocumented is invisible; one that is documented and no longer exists is
/// worse, because someone writes it into a CI script and finds out from an exit code. Neither shows up in a
/// build, a test run, or a review of the code that changed — the docs are a different file, and nothing was
/// checking them. This release series added eleven options and a rule; one option had already slipped
/// (`--gitignore-dir`), which is the whole argument for asserting it rather than remembering it.
///
/// Modelled on <c>OutputSchemaDriftTests</c>, which does the same job for the JSON contract: compare the
/// published description against the code by reflection, and fail the build when they disagree.
/// </summary>
public class DocumentationDriftTests
{
    [Fact]
    public void EveryCommandLineOption_IsInTheReadmeReference()
    {
        var undocumented = LongOptionNames()
            .Where(name => !Readme().Contains($"--{name}", StringComparison.Ordinal))
            .ToList();

        undocumented
            .Should()
            .BeEmpty("an option nobody can find is an option that does not exist for most users");
    }

    [Fact]
    public void EveryOptionInTheReadmeReference_StillExists()
    {
        // The direction that costs someone an afternoon: a flag documented after it was renamed or removed.
        var real = LongOptionNames().ToHashSet(StringComparer.Ordinal);

        // Only the options table is scanned — prose elsewhere legitimately mentions flags of other tools,
        // and `dotnet` commands of its own.
        var documented = Regex
            .Matches(OptionsTables(), @"^\|\s*`--(?<name>[a-z0-9-]+)`", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        documented.Should().NotBeEmpty("the tables must actually have been found");
        documented
            .Where(name => !real.Contains(name))
            .Should()
            .BeEmpty("a documented flag that no longer exists is discovered from an exit code");
    }

    [Fact]
    public void EveryExitCode_IsDocumented()
    {
        // The contract a CI script is written against, and the one thing a script cannot discover by trying.
        var readme = Readme();

        foreach (var (name, value) in ExitCodeValues())
        {
            readme
                .Should()
                .Contain(
                    $"| `{value}` | {name} |",
                    $"exit code {value} has to be in the table, named {name} so the two cannot drift apart"
                );
        }
    }

    [Fact]
    public void EveryAnalysisRule_IsInTheRuleReference()
    {
        // --explain reads from the catalog, which is already asserted complete; this is the published page
        // that a SARIF annotation's help link points at.
        var reference = File.ReadAllText(RepositoryFile(Path.Combine("docs", "rules.md")));

        var missing = Enum.GetValues<AnalysisIssueCode>()
            .Where(code => code != AnalysisIssueCode.Unknown)
            .Select(code => code.ToString())
            .Where(name => !reference.Contains(name, StringComparison.Ordinal))
            .ToList();

        missing.Should().BeEmpty("a rule with no published documentation cannot be acted on");
    }

    private static IEnumerable<string> LongOptionNames()
    {
        return typeof(Options)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<OptionAttribute>())
            .Where(option => option is not null && !string.IsNullOrEmpty(option.LongName))
            .Select(option => option!.LongName);
    }

    private static IEnumerable<(string Name, int Value)> ExitCodeValues()
    {
        return typeof(ExitCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType.Name: nameof(Int32) })
            .Select(field => (field.Name, (int)field.GetRawConstantValue()!));
    }

    /// <summary>Just the pipe tables whose first column is an option, so prose cannot confuse the check.</summary>
    private static string OptionsTables()
    {
        return string.Join(
            "\n",
            Readme()
                .Split('\n')
                .Where(line => line.TrimStart().StartsWith("| `--", StringComparison.Ordinal))
        );
    }

    private static string Readme() => File.ReadAllText(RepositoryFile("README.md"));

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} above the test output.");
    }
}
