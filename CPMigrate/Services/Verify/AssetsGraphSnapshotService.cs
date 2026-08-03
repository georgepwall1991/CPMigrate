using CPMigrate.Models;

namespace CPMigrate.Services.Verify;

/// <summary>
/// Captures the resolved dependency graph of every project in scope by restoring and reading each
/// project's <c>project.assets.json</c>.
/// </summary>
/// <remarks>
/// The restore is a plain <c>dotnet restore</c> writing to each project's ordinary <c>obj/</c>. It
/// deliberately does not reuse the scan path's redirected <c>MSBuildProjectExtensionsPath</c>: that
/// redirect is passed as an environment variable, so it applies to every project in the MSBuild graph
/// rather than the one being queried. Any project with a <c>ProjectReference</c> then tells its
/// references to write to the same directory, they collide, and the query comes back describing no
/// frameworks at all — which a caller counting packages reads as a successful scan of an empty project.
/// That regression cost three of Serilog's six projects with no warning; see v3.28.1.
/// </remarks>
public sealed class AssetsGraphSnapshotService : IGraphSnapshotService
{
    private readonly IDotNetCliService _dotNetCli;
    private readonly IDependencyGraphService _dependencyGraph;
    private readonly IConsoleService _console;

    public AssetsGraphSnapshotService(
        IDotNetCliService dotNetCli,
        IDependencyGraphService dependencyGraph,
        IConsoleService console
    )
    {
        _dotNetCli = dotNetCli;
        _dependencyGraph = dependencyGraph;
        _console = console;
    }

    /// <inheritdoc />
    public async Task<GraphSnapshotResult> CaptureAsync(
        string restoreTargetPath,
        IReadOnlyList<string> projectPaths,
        string? basePath
    )
    {
        var (output, succeeded) = await _dotNetCli.RunRestoreAsync(restoreTargetPath);

        if (!succeeded)
        {
            // Nothing is read on a failed restore. The assets still on disk describe whatever state the
            // tree was last restored in — a graph that parses perfectly and describes something that no
            // longer exists. Comparing against it would report a clean run over a broken one.
            return new GraphSnapshotResult(
                RestoreSucceeded: false,
                output,
                new ResolvedGraphSnapshot([], [])
            );
        }

        List<ProjectResolvedGraph> projects = [];
        List<UnreadableProject> unreadable = [];
        var ambiguous = FindAmbiguousProjects(projectPaths, basePath);

        foreach (var projectPath in projectPaths)
        {
            if (ambiguous.TryGetValue(projectPath, out var reason))
            {
                // Fails closed rather than reporting a graph it cannot attribute. Two projects in one
                // directory share a single obj/project.assets.json, so reading "each" of them returns
                // whichever restored last — for both. The duplicate compares equal to itself and a
                // real change in the other project is never seen, which is worse than any error: it is
                // a clean verdict over an unexamined project.
                unreadable.Add(
                    new UnreadableProject(
                        ProjectPackageInfo.ProjectId(basePath, projectPath),
                        reason
                    )
                );
                continue;
            }

            // Read by the path on disk, reported by the path relative to the scan root. An absolute
            // path in the receipt would make it differ between a developer's machine and CI while
            // describing exactly the same result.
            var projectId = ProjectPackageInfo.ProjectId(basePath, projectPath);
            var graph = _dependencyGraph.TryReadResolvedGraph(projectPath);

            if (graph is null)
            {
                // Recorded, never dropped. A snapshot that quietly covers fewer projects than the run
                // intended compares clean against a larger one: the project simply stops being
                // mentioned, and nothing reports the absence.
                // Names the likeliest cause rather than leaving it a mystery. Restore succeeded, so
                // the assets file exists somewhere — a project that sets MSBuildProjectExtensionsPath,
                // BaseIntermediateOutputPath, or ProjectAssetsFile has moved it, and finding it needs
                // full MSBuild evaluation this pass deliberately does not perform. Stated as a
                // limitation rather than implied away, in the same terms as the scan lock's.
                unreadable.Add(
                    new UnreadableProject(
                        projectId,
                        "restore succeeded but wrote no readable obj/project.assets.json here — a "
                            + "project that redirects its intermediate output (MSBuildProjectExtensionsPath, "
                            + "BaseIntermediateOutputPath, ProjectAssetsFile) cannot be verified, because "
                            + "locating its graph needs MSBuild evaluation this pass does not perform"
                    )
                );
                _console.Warning(
                    $"Could not read the resolved graph for {Path.GetFileName(projectPath)}."
                );
                continue;
            }

            projects.Add(graph with { ProjectPath = projectId });
        }

        return new GraphSnapshotResult(
            RestoreSucceeded: true,
            output,
            new ResolvedGraphSnapshot(projects, unreadable)
        );
    }

    /// <summary>
    /// Projects that cannot be measured independently of another project in the same run, mapped to
    /// the reason.
    /// </summary>
    /// <remarks>
    /// Two collisions, both of which silently produce a wrong answer rather than an error.
    ///
    /// <para>
    /// A shared directory shares an assets file: NuGet writes <c>obj/project.assets.json</c> per
    /// directory, not per project, so two project files beside each other overwrite one another and
    /// reading "each" returns whichever restored last, twice. The duplicate compares equal to itself,
    /// and a real change in the other project is never seen. This is the same collision that cost
    /// three of Serilog's six projects in v3.28.1, arriving by a different route.
    /// </para>
    ///
    /// <para>
    /// A shared identity is the reporting equivalent: two projects outside the scan root with the same
    /// file name both fall back to that name, so the diff looks one of them up and finds the other.
    /// </para>
    ///
    /// Both fail closed. A verification that cannot tell two projects apart has not verified either.
    /// </remarks>
    private static Dictionary<string, string> FindAmbiguousProjects(
        IReadOnlyList<string> projectPaths,
        string? basePath
    )
    {
        Dictionary<string, string> ambiguous = new(StringComparer.Ordinal);

        Collect(
            projectPaths.GroupBy(
                (string path) => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase
            ),
            "shares a directory, and therefore a single obj/project.assets.json, with another project "
                + "in this solution, so its resolved graph cannot be read independently"
        );

        Collect(
            projectPaths.GroupBy(
                (string path) => ProjectPackageInfo.ProjectId(basePath, path),
                StringComparer.OrdinalIgnoreCase
            ),
            "cannot be told apart from another project in this solution, which shares the name it is "
                + "reported under"
        );

        return ambiguous;

        void Collect(IEnumerable<IGrouping<string, string>> groups, string reason)
        {
            foreach (var group in groups.Where((IGrouping<string, string> g) => g.Count() > 1))
            {
                foreach (var path in group)
                {
                    ambiguous[path] = reason;
                }
            }
        }
    }
}
