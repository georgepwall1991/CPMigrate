using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
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

    [Fact]
    public void Schema_CoversEveryRetentionConfigProperty()
    {
        var documented = SchemaProperties()["retention"]
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name);

        var modelled = typeof(RetentionConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(
                property =>
                    property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name
            );

        documented
            .Should()
            .BeEquivalentTo(
                modelled,
                "nested retention settings are part of the public configuration contract too"
            );
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
    public void Schema_OffersTheCanonicalPolicyValuesForCompletion()
    {
        var offered = RulePolicyValue()
            .GetProperty("anyOf")[0]
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        offered
            .Should()
            .BeEquivalentTo(
                Enum.GetNames<AnalysisSeverity>().Append(RulePolicy.DisableKeyword),
                "the completion list is the canonical spelling of every value"
            );
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("  {0}  ")]
    public void Schema_AcceptsEveryCasingTheParserAccepts(string format)
    {
        // Enumerating casings in an enum is unwinnable — "nOnE" is as valid to the parser as
        // "none". The pattern closes the class instead, and this asserts the two agree rather than
        // chasing spellings one bug report at a time.
        var pattern = new Regex(
            RulePolicyValue().GetProperty("anyOf")[1].GetProperty("pattern").GetString()!
        );

        foreach (var canonical in CanonicalPolicyValues())
        {
            foreach (var spelling in Casings(canonical))
            {
                var candidate = string.Format(CultureInfo.InvariantCulture, format, spelling);
                var (policy, error) = RulePolicy.Parse(new[] { $"LicenseRisk={candidate}" });

                error.Should().BeNull($"the parser accepts '{candidate}'");
                policy.Should().NotBeNull();
                pattern
                    .IsMatch(candidate)
                    .Should()
                    .BeTrue($"the schema must not reject '{candidate}', which the tool runs");
            }
        }
    }

    private static IEnumerable<string> Casings(string value)
    {
        yield return value;
        yield return value.ToLowerInvariant();
        yield return value.ToUpperInvariant();
        // Alternating case: the spelling nobody writes deliberately, and exactly the one an
        // enum-of-variants misses.
        yield return string.Concat(
            value.Select(
                (character, index) =>
                    index % 2 == 0
                        ? char.ToUpperInvariant(character)
                        : char.ToLowerInvariant(character)
            )
        );
    }

    private static IEnumerable<string> CanonicalPolicyValues()
    {
        return Enum.GetNames<AnalysisSeverity>().Append(RulePolicy.DisableKeyword);
    }

    private static JsonElement RulePolicyValue()
    {
        return Schema().GetProperty("definitions").GetProperty("rulePolicyValue");
    }

    [Fact]
    public void Schema_OffersNothingTheParserWouldReject()
    {
        // The other direction: every spelling the schema blesses has to actually work.
        var offered = RulePolicyValue()
            .GetProperty("anyOf")[0]
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!);

        foreach (var value in offered)
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
