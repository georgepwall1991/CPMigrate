using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The README's exit-code table is the contract a CI gate is written against. Nothing else in the
/// repo forces it to match <see cref="ExitCodes"/> — a code added without a table row, or renamed,
/// would leave every script that branches on these numbers reading from stale documentation.
/// </summary>
public class ExitCodeContractTests
{
    private static readonly Dictionary<int, string> DocumentedCodes = ExtractReadmeTable();

    [Fact]
    public void EveryConstant_IsListedWithItsValueAndName()
    {
        var constants = typeof(ExitCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(int))
            .Select(field => (Name: field.Name, Value: (int)field.GetRawConstantValue()!))
            .ToList();

        constants.Should().NotBeEmpty();

        foreach (var (name, value) in constants)
        {
            DocumentedCodes.Should().ContainKey(
                value,
                $"exit code {value} ({name}) is part of the published contract and must have a README row"
            );
        }

        // And nothing documented that the code no longer defines.
        var definedValues = constants.Select(entry => entry.Value).ToHashSet();
        DocumentedCodes.Keys.Should().OnlyContain(
            value => definedValues.Contains(value),
            "the README must not advertise an exit code the tool never returns"
        );
    }

    [Fact]
    public void DocumentedRows_CarryTheConstantNames()
    {
        var names = typeof(ExitCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(int))
            .Select(field => field.Name)
            .ToList();

        foreach (var name in names)
        {
            DocumentedCodes.Values.Should().Contain(
                documentedName => documentedName == name,
                $"the README row for '{name}' must use the constant's exact name"
            );
        }
    }

    /// <summary>Parses the `| `0` | Name | …` rows out of the README's exit-codes table.</summary>
    private static Dictionary<int, string> ExtractReadmeTable()
    {
        var readme = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "README.md")
        );

        var section = readme[readme.IndexOf("## 🚪 Exit codes", StringComparison.Ordinal)..];

        // Slice at the next heading rather than the first "---": the table's own alignment row is
        // made of dashes and would cut the parse off before any data row.
        var nextHeading = section.IndexOf("\n## ", StringComparison.Ordinal);
        var table = nextHeading > 0 ? section[..nextHeading] : section;

        var documented = new Dictionary<int, string>();
        foreach (
            Match row in Regex.Matches(
                table,
                @"^\|\s*`(\d+)`\s*\|\s*(\w+)\s*\|",
                RegexOptions.Multiline
            )
        )
        {
            var value = int.Parse(row.Groups[1].Value);
            documented
                .TryAdd(value, row.Groups[2].Value)
                .Should()
                .BeTrue($"the README must not carry two rows for exit code {value}");
        }

        return documented;
    }

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "README.md")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
