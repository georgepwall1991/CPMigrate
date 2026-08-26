using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable answer to a <c>--why</c> question: the same analysis
/// <see cref="PackageOriginService"/> renders as a tree, serialized as one JSON document so a CI
/// script can consume it.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>why</c>.</param>
/// <param name="PackageId">The package asked about, as the request carried it.</param>
/// <param name="Status"><c>found</c> when any project declares or sees the package, else <c>not-found</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets the
/// same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="Projects">Per-project findings, ordered by path.</param>
/// <param name="Summary">Counts per relationship kind, plus how much of the workspace went unexamined.</param>
/// <param name="VersionsInUse">
/// Every distinct resolved version and the projects using it; more than one entry is version drift.
/// </param>
/// <param name="Suggestions">
/// Near-miss package names present in the workspace, when the package was not found.
/// </param>
public sealed record PackageOriginPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("projects")] IReadOnlyList<PackageOriginProjectPayload> Projects,
    [property: JsonPropertyName("summary")] PackageOriginSummaryPayload Summary,
    [property: JsonPropertyName("versionsInUse")]
        IReadOnlyList<PackageOriginVersionUsagePayload> VersionsInUse,
    [property: JsonPropertyName("suggestions")] IReadOnlyList<string> Suggestions
);

/// <summary>
/// The machine-readable answer to a multi-package <c>--why A,B,C</c> question: every package's
/// answer over the shared scan, serialized as one JSON document so a CI job auditing N packages
/// pays for one invocation.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">
/// The command that produced it — always <c>why-many</c>. A discriminator of its own rather than
/// <c>why</c>: a consumer routing by <c>operation</c> must land here, not on
/// <see cref="PackageOriginPayload"/>, whose shape this document deliberately does not share.
/// </param>
/// <param name="PackageIds">The packages asked about, as the request carried them, in order.</param>
/// <param name="ExitCode">
/// The code the process exits with — the worst of the per-package answers folded by
/// <see cref="PackageOriginService.CombineExitCodes"/> — mirrored here so a consumer reading the
/// document alone gets the same verdict a shell script would. Each result carries its own code
/// too, so a caller can tell which package drove it.
/// </param>
/// <param name="Results">One answer per asked-about package, in the order the IDs were passed.</param>
public sealed record MultiWhyPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("packageIds")] IReadOnlyList<string> PackageIds,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("results")]
        IReadOnlyList<WhyAnswerPayload> Results
);

/// <summary>
/// One package's answer inside a <see cref="MultiWhyPayload"/>: everything a
/// <see cref="PackageOriginPayload"/> says about the package itself, minus the fields that
/// describe the document as a whole.
/// </summary>
/// <param name="PackageId">The package asked about, as the request carried it.</param>
/// <param name="Status"><c>found</c> when any project declares or sees the package, else <c>not-found</c>.</param>
/// <param name="ExitCode">
/// The code this package's answer settled on, mapped exactly as a single-package run would map it.
/// </param>
/// <param name="Projects">Per-project findings, ordered by path.</param>
/// <param name="Summary">Counts per relationship kind, plus how much of the workspace went unexamined.</param>
/// <param name="VersionsInUse">
/// Every distinct resolved version and the projects using it; more than one entry is version drift.
/// </param>
/// <param name="Suggestions">
/// Near-miss package names present in the workspace, when the package was not found.
/// </param>
public sealed record WhyAnswerPayload(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("projects")] IReadOnlyList<PackageOriginProjectPayload> Projects,
    [property: JsonPropertyName("summary")] PackageOriginSummaryPayload Summary,
    [property: JsonPropertyName("versionsInUse")]
        IReadOnlyList<PackageOriginVersionUsagePayload> VersionsInUse,
    [property: JsonPropertyName("suggestions")] IReadOnlyList<string> Suggestions
);

/// <summary>One project's relationship to the traced package, for JSON consumers.</summary>
/// <param name="ProjectPath">Full path to the project file, as scanned.</param>
/// <param name="RelativePath">
/// The project path relative to the scan root, forward-slashed, so a payload parsed on another
/// machine still names its projects. A bare file name when no scan root is known.
/// </param>
/// <param name="Kind">
/// The strongest relationship found: <c>centralPin</c>, <c>inlineVersion</c>, <c>inherited</c>,
/// <c>updateOnly</c>, <c>transitiveOnly</c>, <c>notPresent</c>, or <c>unreadable</c>.
/// </param>
/// <param name="InlineVersion">
/// The version pinned inline when <paramref name="Kind"/> is <c>inlineVersion</c>; absent otherwise.
/// </param>
/// <param name="ResolvedVersions">
/// Every distinct version NuGet resolved for this project, normalized for comparison. Absent when
/// the project could not be read — an unreadable project asserts nothing about versions.
/// </param>
/// <param name="VersionsByTargetFramework">
/// The resolved version per target framework, where a readable resolved graph could say; more than
/// one entry is ordinary for a multi-targeted project. Absent when no graph was available or the
/// project's own scans did not succeed — a graph left on disk by an older restore must not publish
/// version claims about a project this run could not read.
/// </param>
/// <param name="TransitiveIntroducers">
/// Direct packages that pull the target in, when <paramref name="Kind"/> is <c>transitiveOnly</c>.
/// Empty when no resolved graph could say; absent for every other kind.
/// </param>
public sealed record PackageOriginProjectPayload(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("inlineVersion")] string? InlineVersion = null,
    [property: JsonPropertyName("resolvedVersions")]
        IReadOnlyList<string>? ResolvedVersions = null,
    [property: JsonPropertyName("versionsByTargetFramework")]
        IReadOnlyList<PackageOriginFrameworkVersionPayload>? VersionsByTargetFramework = null,
    [property: JsonPropertyName("transitiveIntroducers")]
        IReadOnlyList<string>? TransitiveIntroducers = null
);

/// <summary>The resolved version of the traced package in one target framework.</summary>
/// <param name="TargetFramework">The short framework name, as the assets file keys it.</param>
/// <param name="Version">The resolved version, normalized like every other version here.</param>
public sealed record PackageOriginFrameworkVersionPayload(
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("version")] string Version
);

/// <summary>One distinct version of the traced package, and who resolves to it.</summary>
/// <param name="Version">The normalized version.</param>
/// <param name="Projects">Paths of the projects resolving to it, relative to the scan root.</param>
public sealed record PackageOriginVersionUsagePayload(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects
);

/// <summary>Workspace counts for a <c>--why</c> run.</summary>
/// <param name="ProjectCount">How many projects workspace discovery found.</param>
/// <param name="Direct">Projects declaring the package directly, inline or against the central pin.</param>
/// <param name="UpdateOnly">Projects amending an inherited reference via <c>PackageReference Update</c>.</param>
/// <param name="Transitive">Projects seeing the package only through another package's closure.</param>
/// <param name="Inherited">Projects resolving the package top-level while declaring it elsewhere.</param>
/// <param name="NotPresent">Projects that neither declare nor see the package.</param>
/// <param name="Unreadable">Projects that could not be examined at all.</param>
/// <param name="FailedScans">
/// How many of the discovered projects could not be scanned — the number behind exit code 8.
/// </param>
public sealed record PackageOriginSummaryPayload(
    [property: JsonPropertyName("projectCount")] int ProjectCount,
    [property: JsonPropertyName("direct")] int Direct,
    [property: JsonPropertyName("updateOnly")] int UpdateOnly,
    [property: JsonPropertyName("transitive")] int Transitive,
    [property: JsonPropertyName("inherited")] int Inherited,
    [property: JsonPropertyName("notPresent")] int NotPresent,
    [property: JsonPropertyName("unreadable")] int Unreadable,
    [property: JsonPropertyName("failedScans")] int FailedScans
);

/// <summary>
/// Serializes a <see cref="PackageOriginReport"/> into the single JSON document the
/// <c>--why --output Json</c> contract promises on stdout.
///
/// <para>
/// Deliberately separate from <see cref="PackageOriginService"/>'s console rendering, which stays
/// untouched: the two paths share analysis and exit codes, never output.
/// </para>
/// </summary>
internal static class PackageOriginJsonWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the payload. <paramref name="exitCode"/> is the value
    /// <see cref="PackageOriginService.MapExitCode"/> settled for the same request and report —
    /// passed in rather than recomputed so the document cannot disagree with the process.
    /// </summary>
    public static string Serialize(
        PackageOriginRequest request,
        PackageOriginReport report,
        int exitCode
    )
    {
        var payload = new PackageOriginPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "why",
            report.PackageId,
            report.Found ? "found" : "not-found",
            exitCode,
            ToProjectPayloads(request, report),
            ToSummaryPayload(request, report),
            ToVersionUsagePayloads(report),
            report.Suggestions
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    /// <summary>
    /// Builds and serializes the multi-package payload. <paramref name="exitCode"/> is the value
    /// <see cref="PackageOriginService.CombineExitCodes"/> settled over the per-answer codes —
    /// passed in rather than recomputed so the document cannot disagree with the process; each
    /// answer's own code is likewise whatever <see cref="PackageOriginService.MapExitCode"/> said.
    /// </summary>
    public static string SerializeMany(
        IReadOnlyList<(PackageOriginRequest Request, PackageOriginReport Report, int ExitCode)>
            answers,
        int exitCode
    )
    {
        var payload = new MultiWhyPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "why-many",
            [.. answers.Select(answer => answer.Report.PackageId)],
            exitCode,
            [
                .. answers.Select(answer => new WhyAnswerPayload(
                    answer.Report.PackageId,
                    answer.Report.Found ? "found" : "not-found",
                    answer.ExitCode,
                    ToProjectPayloads(answer.Request, answer.Report),
                    ToSummaryPayload(answer.Request, answer.Report),
                    ToVersionUsagePayloads(answer.Report),
                    answer.Report.Suggestions
                )),
            ]
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static IReadOnlyList<PackageOriginProjectPayload> ToProjectPayloads(
        PackageOriginRequest request,
        PackageOriginReport report
    )
    {
        var graphsByProject = request.ResolvedGraphs
            .GroupBy(g => g.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // A graph on disk may predate a run that could not read the project. Per-framework version
        // claims are published only for projects whose scans actually succeeded this run — the same
        // rule ClassifyProject applies to every other assertion the report makes.
        var scansByProject = (request.ScanOutcomes ?? [])
            .GroupBy(s => s.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return
        [
            .. report.Projects.Select(p => ToProjectPayload(
                p,
                request.PackageId,
                graphsByProject.GetValueOrDefault(p.ProjectPath),
                scansByProject.GetValueOrDefault(p.ProjectPath)
            )),
        ];
    }

    private static PackageOriginSummaryPayload ToSummaryPayload(
        PackageOriginRequest request,
        PackageOriginReport report
    ) =>
        new(
            request.ProjectCount,
            report.Projects.Count(p =>
                p.Kind is PackageOriginKind.InlineVersion or PackageOriginKind.CentralPin
            ),
            report.Projects.Count(p => p.Kind == PackageOriginKind.UpdateOnly),
            report.Projects.Count(p => p.Kind == PackageOriginKind.TransitiveOnly),
            report.Projects.Count(p => p.Kind == PackageOriginKind.Inherited),
            report.Projects.Count(p => p.Kind == PackageOriginKind.NotPresent),
            report.Projects.Count(p => p.Kind == PackageOriginKind.Unreadable),
            request.FailedScanCount
        );

    private static IReadOnlyList<PackageOriginVersionUsagePayload> ToVersionUsagePayloads(
        PackageOriginReport report
    ) =>
    [
        .. report.VersionsInUse.Select(v => new PackageOriginVersionUsagePayload(
            v.Version,
            v.Projects
        )),
    ];

    private static PackageOriginProjectPayload ToProjectPayload(
        PackageOriginProjectReport project,
        string packageId,
        ProjectResolvedGraph? graph,
        PackageOriginProjectScan? scan
    )
    {
        // One version per framework, straight from the graph's own rows. But only when this run's
        // own scans read the project: a graph left in obj/ by an earlier restore says nothing about
        // a project whose resolved scan just failed, and repeating it would dress stale data up as
        // a current answer.
        List<PackageOriginFrameworkVersionPayload>? perFramework = null;
        if (graph is not null && scan is { ResolvedRead: true, DeclarationsRead: true })
        {
            perFramework =
            [
                .. graph
                    .Frameworks.Where(f => f.Resolved)
                    .SelectMany(
                        f =>
                            f.Packages
                                .Where(p =>
                                    p.PackageId.Equals(
                                        packageId,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                .Take(1),
                        (f, p) => new PackageOriginFrameworkVersionPayload(
                            f.TargetFramework,
                            VersionText.Normalize(p.Version)
                        )
                    ),
            ];
            if (perFramework.Count == 0)
            {
                perFramework = null;
            }
        }

        return new PackageOriginProjectPayload(
            project.ProjectPath,
            project.DisplayPath,
            KindName(project.Kind),
            project.InlineVersion,
            project.ResolvedVersions,
            perFramework,
            project.Kind == PackageOriginKind.TransitiveOnly ? project.TransitiveIntroducers : null
        );
    }

    /// <summary>
    /// Spells each kind in camelCase rather than trusting the enum's default serialization: the
    /// names are part of the published contract, so they are written out here where a reader — and
    /// a test — can see them all.
    /// </summary>
    private static string KindName(PackageOriginKind kind) =>
        kind switch
        {
            PackageOriginKind.NotPresent => "notPresent",
            PackageOriginKind.Unreadable => "unreadable",
            PackageOriginKind.TransitiveOnly => "transitiveOnly",
            PackageOriginKind.Inherited => "inherited",
            PackageOriginKind.UpdateOnly => "updateOnly",
            PackageOriginKind.CentralPin => "centralPin",
            _ => "inlineVersion",
        };
}
