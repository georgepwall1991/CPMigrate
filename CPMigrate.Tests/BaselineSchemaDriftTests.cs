using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The baseline file is a reviewer-visible record of accepted debt, and writers and readers meet
/// only through <c>schemas/cpmigrate-baseline.schema.json</c>. A writer field without a schema
/// update (or the reverse) breaks that meeting silently.
///
/// Rather than pulling in a JSON-schema validator, these tests compare the schema against the
/// model by reflection — the same approach <c>OutputSchemaDriftTests</c> uses for the output
/// contract. It checks the property that actually matters (the schema and the code describe the
/// same shape) and catches the actual failure mode (someone adds a field and forgets the schema).
/// </summary>
public class BaselineSchemaDriftTests
{
    [Fact]
    public void Schema_DescribesExactlyTheFieldsTheModelEmits()
    {
        var modelled = JsonPropertyNames(typeof(BaselineFile)).ToHashSet(StringComparer.Ordinal);
        var documented = Schema().GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        documented.Should().BeEquivalentTo(modelled, "the schema and the model must describe the same file");
    }

    [Fact]
    public void Schema_RequiresTheIdentifyingFields()
    {
        Schema().GetProperty("required").EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .BeEquivalentTo("baselineVersion", "fingerprintVersion", "findings");
    }

    [Fact]
    public void Schema_FindingItems_MatchTheModel()
    {
        var items = Schema().GetProperty("properties").GetProperty("findings").GetProperty("items");

        items.GetProperty("required").EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .BeEquivalentTo("fingerprint", "issueCode", "package", "severity", "projects");

        items.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(JsonPropertyNames(typeof(BaselineFinding)));
    }

    [Fact]
    public void Schema_SeverityEnum_MatchesTheCode()
    {
        var severity = Schema().GetProperty("properties").GetProperty("findings")
            .GetProperty("items").GetProperty("properties").GetProperty("severity");

        severity.GetProperty("enum").EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .BeEquivalentTo(Enum.GetNames<AnalysisSeverity>());
    }

    [Fact]
    public void Schema_BaselineVersionConst_MatchesCurrentVersion()
    {
        // The const is what rejects a baseline from a newer format rather than silently
        // matching nothing; it has to move exactly when the model version does.
        Schema().GetProperty("properties").GetProperty("baselineVersion")
            .GetProperty("const").GetString()
            .Should()
            .Be(BaselineFile.CurrentVersion);
    }

    [Fact]
    public void Schema_RejectsAdditionalProperties()
    {
        // additionalProperties: false is what turns an unknown field from a silent ignore
        // into a validation failure an editor surfaces.
        Schema().GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        Schema().GetProperty("properties").GetProperty("findings")
            .GetProperty("items").GetProperty("additionalProperties")
            .GetBoolean().Should().BeFalse();
    }

    private static IEnumerable<string> JsonPropertyNames(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name);
    }

    private static JsonElement Schema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "schemas",
                "cpmigrate-baseline.schema.json"
            );
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate schemas/cpmigrate-baseline.schema.json.");
    }
}
