using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The published output schema is what a consumer writes their parser against, so a stale one is
/// worse than none: it documents fields that do not exist and omits ones that do.
///
/// Rather than pulling in a JSON-schema validator, these tests compare the schema against the model
/// by reflection — the same approach that already caught the config schema missing an enum value. It
/// checks the property that actually matters (the schema and the code describe the same shape) and
/// catches the actual failure mode (someone adds a field and forgets the schema).
/// </summary>
public class OutputSchemaDriftTests
{
    /// <summary>Model types and the schema location describing each.</summary>
    public static TheoryData<string, string> DocumentedTypes() =>
        new()
        {
            { nameof(OperationResult), "" },
            { nameof(OperationSummary), "summary" },
            { nameof(AnalysisIssueInfo), "analysisIssue" },
            { nameof(FixInfo), "fix" },
            { nameof(PackageUpdateInfo), "packageUpdate" },
            { nameof(PropsFileInfo), "propsFile" },
            { nameof(BackupInfo), "backup" },
        };

    [Theory]
    [MemberData(nameof(DocumentedTypes))]
    public void Schema_DescribesExactlyTheFieldsTheModelEmits(string typeName, string definition)
    {
        var modelled = JsonPropertyNames(typeName);
        var documented = SchemaPropertyNames(definition);

        documented
            .Should()
            .BeEquivalentTo(
                modelled,
                $"the published schema is what consumers parse against, so {typeName} must match it exactly"
            );
    }

    [Theory]
    [MemberData(nameof(DocumentedTypes))]
    public void Schema_RejectsUnknownProperties(string typeName, string definition)
    {
        // Without this a consumer validating against the schema would silently accept a payload with
        // a misspelled or hallucinated field.
        _ = typeName;

        Definition(definition).GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Schema_SeverityEnumMatchesTheCode()
    {
        var documented = Schema()
            .GetProperty("definitions")
            .GetProperty("severity")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        documented.Should().BeEquivalentTo(Enum.GetNames<AnalysisSeverity>());
    }

    [Fact]
    public void Schema_FailOnEnumMatchesTheCode()
    {
        var documented = Definition("summary")
            .GetProperty("properties")
            .GetProperty("failOnSeverity")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        documented.Should().BeEquivalentTo(Enum.GetNames<FailOnSeverity>());
    }

    [Fact]
    public void Schema_DocumentsEveryOperationNameTheRouterCanEmit()
    {
        // A consumer switching on `operation` needs the list to be complete, and the names are
        // produced by string literals in the router rather than by an enum — so nothing but a test
        // keeps them in step.
        var documented = Schema()
            .GetProperty("properties")
            .GetProperty("operation")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        documented
            .Should()
            .Contain(
                new[]
                {
                    "analyze",
                    "migrate",
                    "rollback",
                    "update-packages",
                    "batch-analyze",
                    "batch-migrate",
                }
            );
    }

    [Fact]
    public void Schema_RequiresTheFieldsEveryPayloadCarries()
    {
        var required = Schema()
            .GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        required
            .Should()
            .Contain(
                new[]
                {
                    "outputSchemaVersion",
                    "version",
                    "operation",
                    "success",
                    "exitCode",
                    "summary",
                },
                "these are set on every payload regardless of command"
            );
    }

    [Fact]
    public void Schema_IdMatchesItsPublishedLocation()
    {
        // A $id that does not resolve makes the schema unusable as a $ref target.
        Schema()
            .GetProperty("$id")
            .GetString()
            .Should()
            .EndWith("schemas/cpmigrate-output.schema.json");
    }

    [Fact]
    public void SerializedPayload_ContainsNothingTheSchemaDoesNotDocument()
    {
        // Reflection proves the model and schema agree. This proves the *serializer* does too: a
        // naming policy or a converter could emit a key neither of them declares.
        var payload = new OperationResult
        {
            Operation = "analyze",
            Success = false,
            ExitCode = ExitCodes.AnalysisIssuesFound,
            Summary = new OperationSummary
            {
                IssuesFound = 1,
                FailOnSeverity = nameof(CPMigrate.FailOnSeverity.High),
                IssuesAtOrAboveThreshold = 0,
                HighestSeverity = nameof(AnalysisSeverity.Moderate),
                ScanFailures = 0,
                DeepScanFailures = 0,
            },
            AnalysisIssues =
            [
                new AnalysisIssueInfo
                {
                    Type = "Version Inconsistencies",
                    IssueCode = nameof(AnalysisIssueCode.VersionInconsistency),
                    Severity = nameof(AnalysisSeverity.Moderate),
                    Package = "Newtonsoft.Json",
                    Description = "13.0.1, 12.0.3",
                    AffectedProjects = ["src/Api/Api.csproj"],
                    Metadata = new Dictionary<string, string> { ["extra"] = "detail" },
                },
            ],
            PropsFile = new PropsFileInfo { Path = "Directory.Packages.props" },
            Backup = new BackupInfo { Path = ".cpmigrate_backup", FilesBackedUp = 2 },
        };

        var json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }
        );

        using var document = JsonDocument.Parse(json);
        var documented = SchemaPropertyNames(string.Empty).ToHashSet(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            documented
                .Should()
                .Contain(property.Name, $"the schema must document every emitted key");
        }

        var summaryKeys = SchemaPropertyNames("summary").ToHashSet(StringComparer.Ordinal);
        foreach (var property in document.RootElement.GetProperty("summary").EnumerateObject())
        {
            summaryKeys.Should().Contain(property.Name);
        }

        var issueKeys = SchemaPropertyNames("analysisIssue").ToHashSet(StringComparer.Ordinal);
        foreach (var property in document.RootElement.GetProperty("analysisIssues")[0].EnumerateObject())
        {
            issueKeys.Should().Contain(property.Name);
        }
    }

    private static IEnumerable<string> JsonPropertyNames(string typeName)
    {
        var type =
            typeof(OperationResult).Assembly.GetTypes().FirstOrDefault(t => t.Name == typeName)
            ?? throw new InvalidOperationException($"Model type {typeName} not found.");

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name);
    }

    private static IEnumerable<string> SchemaPropertyNames(string definition)
    {
        return Definition(definition)
            .GetProperty("properties")
            .EnumerateObject()
            .Select(p => p.Name);
    }

    private static JsonElement Definition(string definition)
    {
        var schema = Schema();

        return string.IsNullOrEmpty(definition)
            ? schema
            : schema.GetProperty("definitions").GetProperty(definition);
    }

    private static JsonElement Schema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "schemas",
                "cpmigrate-output.schema.json"
            );
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate schemas/cpmigrate-output.schema.json.");
    }
}
