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
        // Against the reference table, not the whole file. Searching everywhere let an option mentioned only
        // in an example count as documented — and the reverse check below cannot catch that, because it only
        // validates rows that already exist. Cross-review caught it.
        var documented = DocumentedOptionNames();

        LongOptionNames()
            .Where(name => !documented.Contains(name))
            .Should()
            .BeEmpty(
                "an option missing from the reference table is one nobody can find, whatever the prose says"
            );
    }

    [Fact]
    public void EveryOptionInTheReadmeReference_StillExists()
    {
        // The direction that costs someone an afternoon: a flag documented after it was renamed or removed.
        var real = LongOptionNames().ToHashSet(StringComparer.Ordinal);

        var documented = DocumentedOptionNames();

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

        // Its own section, not a mention anywhere in the file. A rule whose page was deleted but which is
        // still named in a cross-reference would otherwise pass, which is the drift this exists to catch.
        // Cross-review caught it.
        var sections = Regex
            .Matches(reference, @"^##+\s+(?<rule>\w+)\s*$", RegexOptions.Multiline)
            .Select(match => match.Groups["rule"].Value)
            .ToHashSet(StringComparer.Ordinal);

        sections.Should().NotBeEmpty("the rule sections must actually have been found");

        Enum.GetValues<AnalysisIssueCode>()
            .Where(code => code != AnalysisIssueCode.Unknown)
            .Select(code => code.ToString())
            .Where(name => !sections.Contains(name))
            .Should()
            .BeEmpty("a rule with no section of its own cannot be acted on from a help link");
    }


    /// <summary>
    /// The option names the README's reference tables document — the rows, not the prose. An option
    /// mentioned only in an example is not documented in the sense that matters: nobody scanning the
    /// reference for it will find it.
    /// </summary>
    private static HashSet<string> DocumentedOptionNames()
    {
        // The argument placeholder lives inside the backticks in some rows — `--explain <RuleId>` — so the
        // name is followed by either the closing backtick or a space. Requiring the backtick made --explain
        // look undocumented when it is documented perfectly well.
        return Regex
            .Matches(
                OptionsTables(),
                @"^\|\s*`--(?<name>[a-z0-9-]+)[ `]",
                RegexOptions.Multiline
            )
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
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
