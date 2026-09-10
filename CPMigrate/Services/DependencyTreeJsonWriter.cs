using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The machine-readable form of <c>--tree</c>: the same scan the console renders as an ASCII
/// tree, serialized as one JSON document so a CI script can consume it.
/// </summary>
/// <param name="OutputSchemaVersion">JSON contract version for this payload.</param>
/// <param name="Version">CPMigrate version that produced this result.</param>
/// <param name="Operation">The command that produced it — always <c>tree</c>.</param>
/// <param name="ExitCode">
/// The code the process exits with, mirrored here so a consumer reading the document alone gets
/// the same verdict a shell script would. See <see cref="ExitCodes"/>.
/// </param>
/// <param name="Projects">Every discovered project, in path order — including ones whose scan
/// failed or that declare no packages, so an absent project is never mistaken for an empty one.</param>
/// <param name="Summary">Workspace counts, including how much of it went unexamined.</param>
public sealed record TreeReportPayload(
    [property: JsonPropertyName("outputSchemaVersion")] string OutputSchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("projects")] IReadOnlyList<TreeProjectPayload> Projects,
    [property: JsonPropertyName("summary")] TreeSummaryPayload Summary
);

/// <summary>One project's package lists, for JSON consumers.</summary>
/// <param name="ProjectPath">Full path to the project file, as scanned.</param>
/// <param name="RelativePath">
/// The project path relative to the scan root, forward-slashed, so a payload parsed on another
/// machine still names its projects. A bare file name when no scan root is known.
/// </param>
/// <param name="Scanned">
/// Whether this project's resolved packages could be read. False means the lists below are empty
/// because the project could not be examined — not because it declares nothing.
/// </param>
/// <param name="Direct">Packages the project declares itself, in name order.</param>
/// <param name="Transitive">Packages arriving only through another package's closure, in name order.</param>
public sealed record TreeProjectPayload(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("scanned")] bool Scanned,
    [property: JsonPropertyName("direct")] IReadOnlyList<TreePackagePayload> Direct,
    [property: JsonPropertyName("transitive")] IReadOnlyList<TreePackagePayload> Transitive
);

/// <summary>One package in a project's tree.</summary>
/// <param name="Name">The package ID.</param>
/// <param name="Version">
/// The version string as scanned. Absent when the declaration carries none — under central package
/// management that is the normal case, and the version lives in Directory.Packages.props.
/// </param>
public sealed record TreePackagePayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version
);

/// <summary>Workspace counts for a <c>--tree</c> run.</summary>
/// <param name="ProjectCount">How many projects workspace discovery found.</param>
/// <param name="Direct">Direct package references across every scanned project.</param>
/// <param name="Transitive">Transitive package references across every scanned project.</param>
/// <param name="FailedScans">
/// How many of the discovered projects could not be scanned. The console tree reports this only as
/// warnings; here it is a number a consumer can gate on, because an unread project is not an empty
/// one.
/// </param>
public sealed record TreeSummaryPayload(
    [property: JsonPropertyName("projectCount")] int ProjectCount,
    [property: JsonPropertyName("direct")] int Direct,
    [property: JsonPropertyName("transitive")] int Transitive,
    [property: JsonPropertyName("failedScans")] int FailedScans
);

/// <summary>
/// Serializes a <c>--tree</c> scan into the single JSON document the
/// <c>--tree --output Json</c> contract promises on stdout.
///
/// <para>
/// Deliberately separate from <see cref="DependencyTreeService"/>'s console rendering: the two
/// paths share the scan, never the output — the same split <see cref="PackageOriginJsonWriter"/>
/// keeps for <c>--why</c>.
/// </para>
/// </summary>
internal static class DependencyTreeJsonWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the payload. <paramref name="exitCode"/> is the value the process
    /// settled on — passed in rather than recomputed so the document cannot disagree with it.
    /// </summary>
    /// <param name="packageInfo">The scanned references.</param>
    /// <param name="projects">
    /// Every project discovery found, with whether its scan succeeded — carried explicitly because
    /// a project that scanned clean and declares nothing must not be reported as unread.
    /// </param>
    /// <param name="exitCode">The code the process exits with.</param>
    public static string Serialize(
        ProjectPackageInfo packageInfo,
        IReadOnlyList<(string Path, bool Scanned)> projects,
        int exitCode
    )
    {
        var referencesByProject = packageInfo
            .References.GroupBy(r => r.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PackageReference>)[.. g],
                StringComparer.OrdinalIgnoreCase
            );

        var projectPayloads = projects
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(p => new TreeProjectPayload(
                p.Path,
                ProjectPackageInfo.ProjectId(packageInfo.BasePath, p.Path),
                p.Scanned,
                ToPackagePayloads(referencesByProject, p.Path, direct: true),
                ToPackagePayloads(referencesByProject, p.Path, direct: false)
            ))
            .ToList();

        var payload = new TreeReportPayload(
            OutputMetadata.SchemaVersion,
            OutputMetadata.CurrentVersion,
            Operation: "tree",
            exitCode,
            projectPayloads,
            new TreeSummaryPayload(
                projects.Count,
                packageInfo.References.Count(r => !r.IsTransitive),
                packageInfo.References.Count(r => r.IsTransitive),
                projects.Count(p => !p.Scanned)
            )
        );

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static IReadOnlyList<TreePackagePayload> ToPackagePayloads(
        IReadOnlyDictionary<string, IReadOnlyList<PackageReference>> referencesByProject,
        string projectPath,
        bool direct
    )
    {
        if (!referencesByProject.TryGetValue(projectPath, out var references))
        {
            return [];
        }

        return
        [
            .. references
                .Where(r => r.IsTransitive != direct)
                .OrderBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new TreePackagePayload(
                    r.PackageName,
                    string.IsNullOrEmpty(r.Version) ? null : r.Version
                )),
        ];
    }
}
