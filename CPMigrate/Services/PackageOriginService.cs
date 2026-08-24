using CPMigrate.Models;
using NuGet.Versioning;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// What the <c>--why</c> scan gathered about one package across a workspace, before rendering.
///
/// <para>
/// <see cref="PackageOriginService.Analyze"/> turns this into a
/// <see cref="PackageOriginReport"/>; keeping the input as data rather than service calls is what
/// makes the analysis testable without a solution on disk.
/// </para>
/// </summary>
/// <param name="PackageId">The package whose origin was asked about, verbatim as typed.</param>
/// <param name="Packages">
/// Resolved references (direct and transitive) plus the declarations read from project files.
/// </param>
/// <param name="ResolvedGraphs">
/// Per-project resolved graphs with dependency edges, used to name which direct package pulls the
/// target in transitively. Projects without a readable graph simply have no introducer names.
/// </param>
/// <param name="ProjectCount">How many projects the workspace discovery found.</param>
/// <param name="FailedScanCount">How many of those could not be scanned at all.</param>
/// <param name="ProjectPaths">
/// Every discovered project path, including ones whose scans produced no package records. Without
/// this a project that references nothing would silently vanish from the report.
/// </param>
/// <param name="ScanOutcomes">
/// Per-project scan status. A project whose resolved scan or declaration scan failed cannot prove
/// anything about the package, so it must be reported as unexamined rather than asserted into an
/// origin it was never read well enough to earn.
/// </param>
public sealed record PackageOriginRequest(
    string PackageId,
    ProjectPackageInfo Packages,
    IReadOnlyList<ProjectResolvedGraph> ResolvedGraphs,
    int ProjectCount,
    int FailedScanCount,
    IReadOnlyList<string>? ProjectPaths = null,
    IReadOnlyList<PackageOriginProjectScan>? ScanOutcomes = null
);

/// <summary>Whether each scan leg could read one project.</summary>
/// <param name="ProjectPath">Full path to the project file.</param>
/// <param name="ResolvedRead">Whether the resolved-package scan (dotnet list package) succeeded.</param>
/// <param name="DeclarationsRead">Whether the declared-reference XML scan succeeded.</param>
public sealed record PackageOriginProjectScan(
    string ProjectPath,
    bool ResolvedRead,
    bool DeclarationsRead
);

/// <summary>
/// How one project relates to the traced package, strongest relationship first: an Include beats an
/// Update-only amendment, and either beats a sighting without a local declaration.
/// </summary>
public enum PackageOriginKind
{
    /// <summary>The project does not reference or see the package at all.</summary>
    NotPresent,

    /// <summary>
    /// The project could not be scanned, so no origin can be proven. Reported rather than guessed:
    /// silence here would read as "checked, absent" about a project nobody managed to open.
    /// </summary>
    Unreadable,

    /// <summary>The package appears only in the resolved graph — some direct dependency pulls it in.</summary>
    TransitiveOnly,

    /// <summary>
    /// The resolved graph lists the package as top-level, but no project file declares it: it came
    /// in through Directory.Build.props, an SDK import, or another imported file.
    /// </summary>
    Inherited,

    /// <summary>
    /// The project only amends an inherited reference via <c>PackageReference Update</c>; it does not
    /// bring the package into the graph itself.
    /// </summary>
    UpdateOnly,

    /// <summary>The project declares the package with an <c>Include</c> and no version of its own.</summary>
    CentralPin,

    /// <summary>The project declares the package with an inline version (or a VersionOverride).</summary>
    InlineVersion,
}

/// <summary>One project's relationship to the traced package.</summary>
/// <param name="ProjectPath">Full path to the project file.</param>
/// <param name="DisplayPath">
/// Path relative to the scan root when one is known, otherwise the file name — two projects named
/// App.csproj in different directories must stay distinguishable everywhere they are shown.
/// </param>
/// <param name="Kind">The strongest relationship found.</param>
/// <param name="InlineVersion">
/// The version the project pins inline when <paramref name="Kind"/> is <see cref="PackageOriginKind.InlineVersion"/>,
/// preferring <c>VersionOverride</c> — under CPM that is the version actually in force.
/// </param>
/// <param name="ResolvedVersions">
/// Every distinct version NuGet resolved for this project, normalized for comparison. More than one
/// is ordinary for a multi-targeted project — one version per target framework — and losing all but
/// the first would hide exactly the intra-project drift the report exists to catch.
/// </param>
/// <param name="TransitiveIntroducers">
/// Direct packages that pull the target in, when <paramref name="Kind"/> is
/// <see cref="PackageOriginKind.TransitiveOnly"/>. Empty when no resolved graph could say.
/// </param>
public sealed record PackageOriginProjectReport(
    string ProjectPath,
    string DisplayPath,
    PackageOriginKind Kind,
    string? InlineVersion = null,
    IReadOnlyList<string>? ResolvedVersions = null,
    IReadOnlyList<string>? TransitiveIntroducers = null
);

/// <summary>One distinct version of the traced package, and who resolves to it.</summary>
public sealed record PackageOriginVersionUsage(
    string Version,
    IReadOnlyList<string> Projects
);

/// <summary>The answer to a <c>--why</c> question, ready to render.</summary>
/// <param name="PackageId">The package asked about.</param>
/// <param name="Found">Whether any project in the workspace declares or sees the package.</param>
/// <param name="Projects">Per-project findings, ordered by path.</param>
/// <param name="VersionsInUse">
/// Every distinct resolved version and the projects using it. More than one entry is version drift.
/// </param>
/// <param name="Suggestions">
/// Near-miss package names present in the workspace, when <paramref name="Found"/> is false.
/// </param>
public sealed record PackageOriginReport(
    string PackageId,
    bool Found,
    IReadOnlyList<PackageOriginProjectReport> Projects,
    IReadOnlyList<PackageOriginVersionUsage> VersionsInUse,
    IReadOnlyList<string> Suggestions
);

/// <summary>
/// Answers "why do I have this package?" — which projects declare it directly, which only inherit
/// it transitively and through what, and whether the versions agree across the workspace.
///
/// <para>
/// Rendering mirrors <see cref="DependencyTreeService"/>: one tree per subject, palette ink only,
/// and everything user-derived escaped before it reaches markup.
/// </para>
/// </summary>
internal sealed class PackageOriginService
{
    private readonly IConsoleService _console;

    public PackageOriginService(IConsoleService console)
    {
        _console = console;
    }

    /// <summary>
    /// Analyzes, renders, and maps the outcome onto exit codes: success when answered, incomplete
    /// analysis when part of the workspace went unexamined — whether or not the package was found,
    /// because absence proven only over half the workspace is not absence — and a validation error
    /// only when every project was read and the package is genuinely not there.
    /// </summary>
    public Task<int> RunAsync(PackageOriginRequest request)
    {
        _console.WriteHeader();
        _console.Banner("PACKAGE ORIGIN");
        _console.WriteLine();

        var report = Analyze(request);

        if (!report.Found)
        {
            var someUnread = request.FailedScanCount > 0;
            _console.Error(
                someUnread
                    ? $"Package '{request.PackageId}' was not found among the projects that could "
                        + $"be scanned, but {request.FailedScanCount} of {request.ProjectCount} "
                        + "project(s) could not be read, so it may still live there."
                    : $"Package '{request.PackageId}' was not declared or resolved by any scanned "
                        + "project."
            );
            foreach (var suggestion in report.Suggestions)
            {
                _console.Dim($"  Did you mean '{suggestion}'?");
            }

            if (report.Suggestions.Count == 0)
            {
                _console.Dim("Run --tree --transitive to list every package in the workspace.");
            }

            return Task.FromResult(
                someUnread ? ExitCodes.IncompleteAnalysis : ExitCodes.ValidationError
            );
        }

        if (request.FailedScanCount > 0)
        {
            _console.Warning(
                $"Could not scan {request.FailedScanCount} of {request.ProjectCount} project(s); "
                    + "findings below cover only what could be read."
            );
        }


        Render(request, report);

        return Task.FromResult(
            request.FailedScanCount > 0 ? ExitCodes.IncompleteAnalysis : ExitCodes.Success
        );
    }

    /// <summary>
    /// Classifies every discovered project against the package and settles the drift verdict.
    ///
    /// Versions are compared normalized ("4.3" and "4.3.0" are the same version), because
    /// manufacturing drift out of two spellings of one release would teach nobody anything. Project
    /// identity comes from the discovered paths, so a project that declares nothing still appears —
    /// "this project does not have it" is part of the answer.
    /// </summary>
    internal static PackageOriginReport Analyze(PackageOriginRequest request)
    {
        var graphsByProject = request.ResolvedGraphs
            .GroupBy(g => g.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var scanByProject = (request.ScanOutcomes ?? [])
            .GroupBy(s => s.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var knownProjectPaths = request.ProjectPaths ?? [];
        List<PackageOriginProjectReport> projects = [];
        foreach (
            var projectPath in knownProjectPaths
                .Concat(request.Packages.References.Select(r => r.ProjectPath))
                .Concat(request.Packages.GetDeclaredReferences().Select(r => r.ProjectPath))
                .Concat(graphsByProject.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        )
        {
            projects.Add(ClassifyProject(request, projectPath, graphsByProject, scanByProject));
        }

        var versionsInUse = projects
            .Where(p => p.ResolvedVersions is { Count: > 0 })
            .SelectMany(
                p => p.ResolvedVersions!,
                (project, version) => (Project: project, Version: version)
            )
            .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PackageOriginVersionUsage(
                g.Key,
                [
                    .. g.Select(item => item.Project.DisplayPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                ]
            ))
            .ToList();

        var knownNames = request
            .Packages.References.Concat(request.Packages.GetDeclaredReferences())
            .Select(r => r.PackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new PackageOriginReport(
            request.PackageId,
            Found: projects.Any(p =>
                p.Kind
                    is PackageOriginKind.TransitiveOnly
                        or PackageOriginKind.Inherited
                        or PackageOriginKind.UpdateOnly
                        or PackageOriginKind.CentralPin
                        or PackageOriginKind.InlineVersion
            ),
            projects,
            versionsInUse,
            SuggestSimilar(request.PackageId, knownNames)
        );
    }

    private static PackageOriginProjectReport ClassifyProject(
        PackageOriginRequest request,
        string projectPath,
        Dictionary<string, ProjectResolvedGraph> graphsByProject,
        Dictionary<string, PackageOriginProjectScan> scanByProject
    )
    {
        if (
            scanByProject.TryGetValue(projectPath, out var scan)
            && (!scan.ResolvedRead || !scan.DeclarationsRead)
        )
        {
            // One leg failed: whatever this project thinks about the package cannot be proven, and
            // a half-read answer (declarations without resolution, or the reverse) would present a
            // guess as a finding. Unexamined is the only honest label.
            return new PackageOriginProjectReport(
                projectPath,
                DisplayPath(projectPath, request.Packages.BasePath),
                PackageOriginKind.Unreadable
            );
        }

        var declarations = request
            .Packages.GetDeclaredReferences()
            .Where(r =>
                string.Equals(r.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                && r.PackageName.Equals(request.PackageId, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        // An Update cannot put a package into the graph; only an Include can. Prefer the strongest
        // fact: an Include decides the kind even when an Update also amends the reference.
        var includes = declarations.Where(r => !r.IsMetadataOnlyUpdate).ToList();
        string? inlineVersion = includes
            .Select(r => r.VersionOverride ?? r.Version)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var resolved = request
            .Packages.References.Where(r =>
                string.Equals(r.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                && r.PackageName.Equals(request.PackageId, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        // A multi-targeted project legitimately resolves to one version per framework. Keeping only
        // the first row would make the report depend on enumeration order and hide intra-project
        // drift, so every distinct spelling is retained.
        var resolvedVersions = resolved
            .Where(r => !string.IsNullOrWhiteSpace(r.Version))
            .Select(r => VersionText.Normalize(r.Version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        PackageOriginKind kind;
        if (includes.Count > 0)
        {
            kind = inlineVersion is not null
                ? PackageOriginKind.InlineVersion
                : PackageOriginKind.CentralPin;
        }
        else if (declarations.Count > 0)
        {
            kind = PackageOriginKind.UpdateOnly;
        }
        else if (resolved.Any(r => !r.IsTransitive))
        {
            // Top-level in the resolved graph yet absent from the project file: the declaration
            // lives somewhere imported — Directory.Build.props, an SDK, a targets file. Calling
            // this transitive would send someone hunting for a referencing package that is not.
            kind = PackageOriginKind.Inherited;
        }
        else
        {
            kind = resolved.Count > 0
                ? PackageOriginKind.TransitiveOnly
                : PackageOriginKind.NotPresent;
        }

        IReadOnlyList<string>? introducers = null;
        if (
            kind == PackageOriginKind.TransitiveOnly
            && graphsByProject.TryGetValue(projectPath, out var graph)
        )
        {
            introducers = FindIntroducers(graph, request.PackageId);
        }

        return new PackageOriginProjectReport(
            projectPath,
            DisplayPath(projectPath, request.Packages.BasePath),
            kind,
            inlineVersion,
            resolvedVersions,
            introducers ?? []
        );
    }

    /// <summary>
    /// Names projects by their path relative to the scan root, forward-slashed, matching the
    /// reporting identity used elsewhere; two projects sharing a file name must remain
    /// distinguishable, so a bare file name is used only when no root is known.
    /// </summary>
    private static string DisplayPath(string projectPath, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return Path.GetFileName(projectPath);
        }

        return Path.GetRelativePath(basePath, projectPath).Replace('\\', '/');
    }

    /// <summary>
    /// Names the direct packages whose dependency closure reaches the target, read from the graph's
    /// own edges rather than guessed from proximity.
    /// </summary>
    private static IReadOnlyList<string> FindIntroducers(ProjectResolvedGraph graph, string packageId)
    {
        SortedSet<string> introducers = new(StringComparer.OrdinalIgnoreCase);

        foreach (var packages in graph.Frameworks.Where(f => f.Resolved).Select(f => f.Packages))
        {
            var byId = packages
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // A package the graph itself flags as direct was declared somewhere this scan did not
            // read; there is no introducer to name, and guessing one would be worse than silence.
            if (!byId.TryGetValue(packageId, out var target) || target.IsDirect)
            {
                continue;
            }

            var introducerIds = packages
                .Where(p => p.IsDirect)
                .Select(direct => direct.PackageId)
                .Where(id => Reaches(id, byId));
            foreach (var id in introducerIds)
            {
                introducers.Add(id);
            }
        }

        return [.. introducers];

        bool Reaches(string rootId, Dictionary<string, ResolvedPackage> byId)
        {
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new(byId.TryGetValue(rootId, out var root) ? root.Dependencies : []);

            while (pending.Count > 0)
            {
                var id = pending.Pop();
                if (id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!visited.Add(id) || !byId.TryGetValue(id, out var node))
                {
                    continue;
                }

                foreach (var dependency in node.Dependencies)
                {
                    pending.Push(dependency);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Near-miss package names for an unknown ID: case-insensitive edit distance within a small
    /// threshold, plus containment either way for long names. Ordered closest first, capped so a
    /// typo cannot bury the answer.
    /// </summary>
    internal static IReadOnlyList<string> SuggestSimilar(string query, IEnumerable<string> knownNames)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var needle = query.Trim().ToLowerInvariant();

        return
        [
            .. knownNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name =>
                {
                    var candidate = name.ToLowerInvariant();
                    return (Name: name, Distance: EditDistance(needle, candidate));
                })
                .Where(item =>
                    // The query itself is never a suggestion: with a partial scan the name can
                    // reach here from a leg that did read, and suggesting it right after saying
                    // "not found" would contradict the verdict above.
                    !item.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
                    && (
                        item.Distance <= 3
                        || item.Name.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                        || needle.Contains(item.Name.ToLowerInvariant(), StringComparison.Ordinal)
                    )
                )
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(item => item.Name),
        ];
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution =
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    substitution
                );
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private void Render(PackageOriginRequest request, PackageOriginReport report)
    {
        var root = new Tree(
            $"[bold {SpectrePalette.Ink.Primary}]{Markup.Escape(report.PackageId)}[/]"
        )
        {
            Guide = TreeGuide.Line,
        };

        foreach (var kind in Enum.GetValues<PackageOriginKind>())
        {
            var group = report.Projects.Where(p => p.Kind == kind).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            var node = root.AddNode(RenderKindLabel(kind, group.Count));
            foreach (
                var project in group.OrderBy(
                    p => p.ProjectPath,
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                node.AddNode(RenderProject(project));
            }
        }

        AnsiConsole.Write(root);
        AnsiConsole.WriteLine();

        RenderDriftVerdict(report);

        var directCount = report.Projects.Count(p =>
            p.Kind is PackageOriginKind.InlineVersion or PackageOriginKind.CentralPin
        );
        var inheritedCount = report.Projects.Count(p => p.Kind == PackageOriginKind.Inherited);
        var updateOnlyCount = report.Projects.Count(p => p.Kind == PackageOriginKind.UpdateOnly);
        var transitiveCount = report.Projects.Count(p => p.Kind == PackageOriginKind.TransitiveOnly);
        var notPresentCount = report.Projects.Count(p => p.Kind == PackageOriginKind.NotPresent);
        var unreadableCount = report.Projects.Count(p => p.Kind == PackageOriginKind.Unreadable);
        _console.Dim(
            $"  {report.Projects.Count} project(s): {directCount} direct, {updateOnlyCount} "
                + $"update-only, {transitiveCount} transitive"
                + (inheritedCount > 0 ? $", {inheritedCount} inherited" : "")
                + (notPresentCount > 0 ? $", {notPresentCount} not present" : "")
                + (unreadableCount > 0 ? $", {unreadableCount} unreadable" : "")
                + "."
        );

        if (request.FailedScanCount > 0)
        {
            _console.Dim(
                $"  {request.FailedScanCount} project(s) could not be read and were not examined."
            );
        }

        _console.WriteLine();
    }

    private static string RenderKindLabel(PackageOriginKind kind, int count)
    {
        var (label, ink) = kind switch
        {
            PackageOriginKind.InlineVersion => (
                "declared directly (inline version)",
                SpectrePalette.Ink.Warning
            ),
            PackageOriginKind.CentralPin => (
                "declared directly (central pin)",
                SpectrePalette.Ink.Success
            ),
            PackageOriginKind.Inherited => (
                "resolved directly, declared outside the project",
                SpectrePalette.Ink.Accent
            ),
            PackageOriginKind.UpdateOnly => (
                "update-only amendment",
                SpectrePalette.Ink.Secondary
            ),
            PackageOriginKind.TransitiveOnly => ("seen transitively", SpectrePalette.Ink.Muted),
            PackageOriginKind.Unreadable => ("could not be read", SpectrePalette.Ink.Warning),
            _ => ("not present", SpectrePalette.Ink.Dim),
        };

        return $"[{ink}]{label} ({count})[/]";
    }

    private static string RenderProject(PackageOriginProjectReport project)
    {
        var nameInk = project.Kind switch
        {
            PackageOriginKind.InlineVersion => SpectrePalette.Ink.Warning,
            PackageOriginKind.CentralPin => SpectrePalette.Ink.Success,
            PackageOriginKind.Inherited => SpectrePalette.Ink.Accent,
            PackageOriginKind.UpdateOnly => SpectrePalette.Ink.Secondary,
            PackageOriginKind.TransitiveOnly => SpectrePalette.Ink.Muted,
            PackageOriginKind.Unreadable => SpectrePalette.Ink.Warning,
            _ => SpectrePalette.Ink.Dim,
        };

        var versions = string.Join(", ", project.ResolvedVersions ?? []);
        var detail = project.Kind switch
        {
            PackageOriginKind.InlineVersion =>
                $" [{SpectrePalette.Ink.Text}]{Markup.Escape(project.InlineVersion!)}[/]",
            PackageOriginKind.CentralPin =>
                $" [{SpectrePalette.Ink.Dim}]→ {Markup.Escape(versions.Length > 0 ? versions : "(unresolved)")}[/]",
            PackageOriginKind.Inherited =>
                $" [{SpectrePalette.Ink.Dim}]{Markup.Escape(versions)}[/]",
            PackageOriginKind.TransitiveOnly when project.TransitiveIntroducers!.Count > 0 =>
                $" [{SpectrePalette.Ink.Dim}]via {Markup.Escape(string.Join(", ", project.TransitiveIntroducers))}[/]",
            PackageOriginKind.TransitiveOnly => $" [{SpectrePalette.Ink.Dim}]introducer unknown[/]",
            _ => string.Empty,
        };

        return $"[{nameInk}]{Markup.Escape(project.DisplayPath)}[/]{detail}";
    }

    private void RenderDriftVerdict(PackageOriginReport report)
    {
        var resolvedVersions = report.VersionsInUse;

        if (resolvedVersions.Count == 0)
        {
            _console.Dim("  No resolved version observed — nothing restored carries this package.");
            return;
        }

        if (resolvedVersions.Count == 1)
        {
            _console.Success(
                $"One version everywhere: {resolvedVersions[0].Version} "
                    + $"({resolvedVersions[0].Projects.Count} project(s))."
            );
            return;
        }

        _console.Warning(
            $"Version drift: {resolvedVersions.Count} different versions are in use."
        );
        foreach (var usage in resolvedVersions.OrderByDescending(v => v.Projects.Count))
        {
            _console.Dim($"  {usage.Version}: {string.Join(", ", usage.Projects)}");
        }
    }
}
