using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The published config schema drives IDE autocomplete and validation for every user's
/// <c>.cpmigrate.json</c>. It is hand-written, so it silently goes stale whenever an option or enum
/// value is added — a real value then shows as an editor error. These tests fail the build instead.
/// </summary>
public class ConfigSchemaDriftTests
{
    [Fact]
    public void Schema_CoversEveryConfigModelProperty()
    {
        var documented = SchemaProperties().Keys;

        var modelled = typeof(ConfigModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name);

        documented.Should().BeEquivalentTo(modelled);
    }

    [Theory]
    [InlineData("outputFormat", typeof(OutputFormat))]
    [InlineData("failOn", typeof(FailOnSeverity))]
    [InlineData("conflictStrategy", typeof(ConflictStrategy))]
    public void Schema_EnumsListEveryValue(string property, Type enumType)
    {
        var documented = SchemaProperties()[property]
            .GetProperty("enum")
            .EnumerateArray()
            .Select(v => v.GetString())
            .ToList();

        documented.Should().BeEquivalentTo(Enum.GetNames(enumType));
    }

    [Fact]
    public void Schema_ListsEveryRuleIdUnderRules()
    {
        // Offering the rule names is the whole reason they are enumerated. A rule added without
        // updating the schema would be a valid policy the editor does not know about.
        var documented = SchemaProperties()["rules"]
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name);

        documented.Should().BeEquivalentTo(Enum.GetNames<AnalysisIssueCode>());
    }

    [Fact]
    public void Schema_AcceptsEveryPolicyValueTheParserAccepts()
    {
        // The parser matches case-insensitively, so a schema that only lists canonical spellings
        // fails a config the tool runs perfectly well — an editor error, or a CI validation step
        // rejecting a working file.
        var documented = Schema()
            .GetProperty("definitions")
            .GetProperty("rulePolicyValue")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToList();

        foreach (
            var canonical in Enum.GetNames<AnalysisSeverity>().Append(RulePolicy.DisableKeyword)
        )
        {
            documented.Should().Contain(canonical);
            documented
                .Should()
                .Contain(
                    canonical.ToLowerInvariant() == canonical
                        ? char.ToUpperInvariant(canonical[0]) + canonical[1..]
                        : canonical.ToLowerInvariant(),
                    "the parser accepts any casing, so the schema must not reject the obvious alternative"
                );
        }
    }

    [Fact]
    public void Schema_RulePolicyValuesAreAllParseable()
    {
        // The other direction: every spelling the schema blesses has to actually work.
        var documented = Schema()
            .GetProperty("definitions")
            .GetProperty("rulePolicyValue")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!);

        foreach (var value in documented)
        {
            var (policy, error) = RulePolicy.Parse(new[] { $"LicenseRisk={value}" });

            error.Should().BeNull($"the schema offers '{value}' as a valid policy value");
            policy.Should().NotBeNull();
        }
    }

    [Fact]
    public void Schema_RejectsUnknownProperties()
    {
        // additionalProperties:false is what makes a typo in a config file visible in the editor.
        Schema().GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    private static Dictionary<string, JsonElement> SchemaProperties()
    {
        return Schema()
            .GetProperty("properties")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value);
    }

    private static JsonElement Schema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "schemas", "cpmigrate.schema.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate schemas/cpmigrate.schema.json.");
    }
}
