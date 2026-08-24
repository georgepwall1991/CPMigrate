using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;
using CPMigrate.Services;
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
    private static readonly (Type Model, string Definition)[] ModelDefinitions =
    [
        (typeof(OperationResult), "singleOperation"),
        (typeof(OperationSummary), "summary"),
        (typeof(AnalysisIssueInfo), "analysisIssue"),
        (typeof(FixInfo), "fix"),
        (typeof(PackageUpdateInfo), "packageUpdate"),
        (typeof(PropsFileInfo), "propsFile"),
        (typeof(BackupInfo), "backup"),
        // The verification receipt is a public contract too. Leaving these nested models out meant
        // a field could be added without the reflection guard noticing that the payload schema was
        // now stale.
        (typeof(VerificationInfo), "verification"),
        (typeof(VerificationChangeInfo), "verificationChange"),
        (typeof(VerificationDecisionInfo), "verificationDecision"),
        // VerificationCandidateInfo is intentionally inline under verificationDecision.candidates;
        // a focused assertion below guards that existing public schema shape without rewriting it.
        (typeof(VerificationIntegrityFailureInfo), "verificationIntegrityFailure"),
        // --batch serializes BatchResult, a different shape entirely. Omitting these let the
        // schema require exitCode and summary on a payload that has neither, so valid batch
        // output failed validation.
        (typeof(BatchResult), "batchOperation"),
        (typeof(SolutionResult), "solutionResult"),
        (typeof(BatchTotals), "batchTotals"),
        // --why serializes its own document, not an OperationResult. Omitting these let the
        // schema reject a payload the tool legitimately emits.
        (typeof(PackageOriginPayload), "whyReport"),
        (typeof(PackageOriginProjectPayload), "whyProject"),
        (typeof(PackageOriginFrameworkVersionPayload), "whyFrameworkVersion"),
        (typeof(PackageOriginVersionUsagePayload), "whyVersionUsage"),
        (typeof(PackageOriginSummaryPayload), "whySummary"),
    ];

    public static TheoryData<string, string> DocumentedTypes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (model, definition) in ModelDefinitions)
        {
            data.Add(model.Name, definition);
        }

        return data;
    }

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
    public void Schema_GuardsEveryModelReachableFromPayloadRoots()
    {
        // --why emits its own document root, so its models are reachable from there, just as the
        // operation and batch models are reachable from OperationResult and BatchResult.
        var reachable = PayloadModelTypes(
            typeof(OperationResult),
            typeof(BatchResult),
            typeof(PackageOriginPayload)
        );

        ModelDefinitions
            .Select(entry => entry.Model)
            .Append(typeof(VerificationCandidateInfo))
            .Should()
            .BeEquivalentTo(
                reachable,
                "every nested payload model needs the same reflection guard as its root"
            );
    }

    [Fact]
    public void Schema_MapsEveryObjectDefinitionToAModel()
    {
        var objectDefinitions = Schema()
            .GetProperty("definitions")
            .EnumerateObject()
            .Where(
                property =>
                    property.Value.TryGetProperty("type", out var type)
                    && type.GetString() == "object"
            )
            .Select(property => property.Name);

        ModelDefinitions
            .Select(entry => entry.Definition)
            .Should()
            .BeEquivalentTo(
                objectDefinitions,
                "an unguarded object definition can drift away from the model it documents"
            );
    }

    [Fact]
    public void Schema_DescribesExactlyTheFieldsTheVerificationCandidateEmits()
    {
        var candidate = VerificationCandidateSchema();

        SchemaPropertyNames(candidate)
            .Should()
            .BeEquivalentTo(
                JsonPropertyNames(nameof(VerificationCandidateInfo)),
                "candidate versions and their declaring projects are part of the verification receipt"
            );
        candidate.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
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
        var documented = Definition("singleOperation")
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
                    // A pre-dispatch rejection can produce a payload for any mode the router can
                    // select, not just the ones that get far enough to report a result — so the
                    // enum has to cover every name in the dispatch table, or a consumer validating
                    // against the schema rejects an otherwise parseable error payload.
                    "update",
                    "interactive",
                    // The failure payloads --why emits when discovery or the run itself fails use
                    // the standard operation shape, so "why" belongs in this enum too.
                    "why",
                    "batch-analyze",
                    "batch-migrate",
                }
            );
    }

    [Fact]
    public void Schema_RequiresTheFieldsEveryPayloadCarries()
    {
        var required = Definition("singleOperation")
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
    public void Schema_AcceptsTheWhyDocumentAsATopLevelShape()
    {
        // The why report is a third root alongside singleOperation and batchOperation; a consumer
        // validating against this schema must not have it fall out of the oneOf.
        var references = Schema()
            .GetProperty("oneOf")
            .EnumerateArray()
            .Select(branch => branch.GetProperty("$ref").GetString())
            .ToList();

        references
            .Should()
            .Contain("#/definitions/whyReport")
            .And.Contain("#/definitions/singleOperation")
            .And.Contain("#/definitions/batchOperation");
    }

    [Fact]
    public void Schema_RequiresTheFieldsEveryWhyDocumentCarries()
    {
        var required = Definition("whyReport")
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
                    "packageId",
                    "status",
                    "exitCode",
                    "projects",
                    "summary",
                    "versionsInUse",
                    "suggestions",
                },
                "these are set on every why document, found or not"
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
            Verification = new VerificationInfo
            {
                Verdict = "failed",
                Passed = false,
                Strict = true,
                RolledBack = true,
                ProjectsRestored = 1,
                ProjectsExpected = 2,
                ResolvedVersions = 3,
                Unchanged = 2,
                Changed = 1,
                Unexplained = 1,
                FailureReason = "one framework was not restored",
                Changes =
                [
                    new VerificationChangeInfo
                    {
                        Project = "src/Api/Api.csproj",
                        TargetFramework = "net10.0",
                        PackageId = "Newtonsoft.Json",
                        Kind = "changed",
                        Before = "12.0.3",
                        After = "13.0.1",
                        Direction = "upgrade",
                        Direct = true,
                        Explanation = "transitiveFallout",
                        CausedBy = "Contoso.Root",
                        Description = "resolved version moved",
                    },
                ],
                Decisions =
                [
                    new VerificationDecisionInfo
                    {
                        PackageId = "Contoso.Root",
                        ResolvedVersion = "2.0.0",
                        Source = "highest",
                        Candidates =
                        [
                            new VerificationCandidateInfo
                            {
                                Version = "1.0.0",
                                Projects = ["src/Api/Api.csproj"],
                            },
                        ],
                    },
                ],
                IntegrityFailures =
                [
                    new VerificationIntegrityFailureInfo
                    {
                        Project = "src/Worker/Worker.csproj",
                        TargetFramework = "net10.0",
                        Reason = "restore failed",
                    },
                ],
            },
        };

        var json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }
        );

        using var document = JsonDocument.Parse(json);
        var documented = SchemaPropertyNames("singleOperation").ToHashSet(StringComparer.Ordinal);

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
        foreach (
            var property in document.RootElement.GetProperty("analysisIssues")[0].EnumerateObject()
        )
        {
            issueKeys.Should().Contain(property.Name);
        }

        var verification = document.RootElement.GetProperty("verification");
        AssertDocumentedKeys(verification, Definition("verification"));
        AssertDocumentedKeys(
            verification.GetProperty("changes")[0],
            Definition("verificationChange")
        );

        var decision = verification.GetProperty("decisions")[0];
        AssertDocumentedKeys(decision, Definition("verificationDecision"));
        AssertDocumentedKeys(decision.GetProperty("candidates")[0], VerificationCandidateSchema());
        AssertDocumentedKeys(
            verification.GetProperty("integrityFailures")[0],
            Definition("verificationIntegrityFailure")
        );
    }

    private static void AssertDocumentedKeys(JsonElement payload, JsonElement schema)
    {
        var documented = SchemaPropertyNames(schema).ToHashSet(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            documented
                .Should()
                .Contain(property.Name, "the schema must document every emitted key");
        }
    }

    private static IReadOnlySet<Type> PayloadModelTypes(params Type[] roots)
    {
        var result = new HashSet<Type>();
        foreach (var root in roots)
        {
            Visit(root);
        }

        return result;

        void Visit(Type candidate)
        {
            candidate = Nullable.GetUnderlyingType(candidate) ?? candidate;
            if (candidate == typeof(string) || candidate.IsPrimitive || candidate.IsEnum)
            {
                return;
            }

            if (candidate.IsArray)
            {
                Visit(candidate.GetElementType()!);
                return;
            }

            if (candidate.IsGenericType)
            {
                foreach (var argument in candidate.GetGenericArguments())
                {
                    Visit(argument);
                }

                return;
            }

            if (candidate.Assembly != typeof(OperationResult).Assembly || !result.Add(candidate))
            {
                return;
            }

            foreach (
                var property in candidate
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            )
            {
                Visit(property.PropertyType);
            }
        }
    }

    private static IEnumerable<string> JsonPropertyNames(string typeName)
    {
        var type =
            typeof(OperationResult).Assembly.GetTypes().FirstOrDefault(t => t.Name == typeName)
            ?? throw new InvalidOperationException($"Model type {typeName} not found.");

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            // [JsonIgnore] properties are computed conveniences, not part of the payload — BatchResult
            // derives an ExitCode that is never serialized.
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name);
    }

    private static IEnumerable<string> SchemaPropertyNames(string definition)
    {
        return SchemaPropertyNames(Definition(definition));
    }

    private static IEnumerable<string> SchemaPropertyNames(JsonElement schema)
    {
        return schema.GetProperty("properties").EnumerateObject().Select(property => property.Name);
    }

    private static JsonElement VerificationCandidateSchema()
    {
        return Definition("verificationDecision")
            .GetProperty("properties")
            .GetProperty("candidates")
            .GetProperty("items");
    }

    private static JsonElement Definition(string definition)
    {
        return Schema().GetProperty("definitions").GetProperty(definition);
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
