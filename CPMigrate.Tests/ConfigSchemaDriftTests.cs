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
