using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// The workspace facts <c>--status</c> reports, collected once so the console dashboard and the
/// JSON document can never disagree about what the run found.
/// </summary>
internal sealed record WorkspaceStatus(
    string Directory,
    IReadOnlyList<string> Solutions,
    int ProjectCount,
    bool CpmEnabled,
    int? CentralPackageCount,
    IReadOnlyList<string> ShadowedPropsFiles,
    bool ConfigPresent,
    bool GitRepository,
    bool GitDirty,
    IReadOnlyList<BackupSetInfo> BackupSets,
    IReadOnlyDictionary<string, int> TargetFrameworks
);

internal sealed class StatusService
{
    private readonly IConsoleService _console;
    private readonly ISolutionDiscovery _solutionDiscovery;

    public StatusService(IConsoleService console, ISolutionDiscovery solutionDiscovery)
    {
        _console = console;
        _solutionDiscovery = solutionDiscovery;
    }

    /// <summary>
    /// Renders the dashboard. Kept for callers that only ever want the terminal form.
    /// </summary>
    public Task<int> RunAsync(string searchPath)
    {
        return RunAsync(searchPath, new Options());
    }

    /// <summary>
    /// Collects the workspace status once, then renders it: the console dashboard normally, the
    /// <c>status</c> JSON document under <c>--output Json</c> — same facts, never two readings.
    /// </summary>
    public async Task<int> RunAsync(string searchPath, Options options)
    {
        var status = Collect(searchPath);

        if (options.Output == OutputFormat.Json)
        {
            // EmitAsync, not EmitFailureAsync: a success document that could not reach its
            // --output-file must not fall back to stdout and exit 0 — a CI job expecting the file
            // would pass with a missing artifact.
            await JsonOutputWriter.EmitAsync(
                StatusJsonWriter.Serialize(status, ExitCodes.Success),
                options,
                _console
            );
            return ExitCodes.Success;
        }

        Render(status);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Reads the workspace once. Everything the dashboard or the document reports comes from here,
    /// so the two renderings cannot drift into two different answers.
    /// </summary>
    internal WorkspaceStatus Collect(string searchPath)
    {
        var dir = Directory.Exists(searchPath)
            ? searchPath
            : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";

        var solutions = _solutionDiscovery.GetSolutionFiles(dir).ToList();
        var backups = GetBackupInfo(dir);
        var isGitRepo = Directory.Exists(Path.Combine(dir, ".git"));
        var hasUnstaged = isGitRepo && HasUnstagedChanges(dir);
        var projectFiles = EnumerateProjectFiles(dir);
        var targetFrameworks = DiscoverTargetFrameworks(projectFiles);

        return new WorkspaceStatus(
            dir,
            solutions,
            projectFiles.Count,
            CpmPackageCount(dir, out var packageCount),
            packageCount,
            [.. GoverningFiles
                .FindConflictingPropsFiles(dir)
                .Select(p =>
                    Path.GetRelativePath(dir, p) is { } rel
                    && !rel.StartsWith("..", StringComparison.Ordinal)
                        ? rel
                        : p)],
            File.Exists(Path.Combine(dir, ".cpmigrate.json")),
            isGitRepo,
            hasUnstaged,
            backups,
            targetFrameworks
        );
    }

    private void Render(WorkspaceStatus status)
    {
        _console.WriteHeader();
        _console.Banner("WORKSPACE STATUS");
        _console.WriteLine();

        // Everything below renders what Collect already read — re-walking the tree here would
        // double the I/O and could disagree with the document the JSON path just emitted.
        _console.WriteStatusDashboard(
            status.Directory,
            [.. status.Solutions],
            [.. status.BackupSets],
            status.GitRepository,
            status.GitDirty,
            new Dictionary<string, int>(status.TargetFrameworks)
        );

        WriteCpmDetails(status);
        WriteConfigDetails(status);
        WriteQuickStats(status);

        _console.WriteLine();
    }

    /// <summary>
    /// Whether a Directory.Packages.props exists, with its PackageVersion count when it could be
    /// read. A file that exists but cannot be read still counts as enabled — the dashboard says
    /// YES either way — but the count stays absent so the document does not invent a number.
    /// </summary>
    private static bool CpmPackageCount(string dir, out int? packageCount)
    {
        packageCount = null;
        // The governing file can sit above the named directory; an ancestor props file means
        // CPM is in effect for the projects under it.
        var propsPath = GoverningFiles.FindNearestPropsFile(dir);
        if (propsPath == null)
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(propsPath);
            packageCount = content.Split("<PackageVersion", StringSplitOptions.None).Length - 1;
        }
        catch
        {
            // Exists but unreadable: enabled, count unknown.
        }

        return true;
    }

    /// <summary>
    /// Project files under <paramref name="dir"/>, honoring the same exclusion set every other
    /// scan uses — the previous <c>Contains("bin/")</c> check also dropped projects under a
    /// directory merely *named* like <c>obin/</c>, and kept <c>node_modules</c> trees.
    /// </summary>
    private static List<string> EnumerateProjectFiles(string dir)
    {
        var projects = new List<string>();
        if (!Directory.Exists(dir))
        {
            return projects;
        }

        CollectProjectFiles(dir, projects, new HashSet<string>(MsBuildProps.PathComparerFor(dir)));
        return projects;
    }

    private static void CollectProjectFiles(
        string directory,
        List<string> projects,
        HashSet<string> visited
    )
    {
        try
        {
            if (!visited.Add(Path.GetFullPath(directory)))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            projects.AddRange(
                Directory.EnumerateFiles(directory, "*.*proj").Where(IsProjectFile)
            );

            foreach (var subDir in Directory
                .EnumerateDirectories(directory)
                .Where(d => !BatchService.DefaultExcludedDirectories.Contains(Path.GetFileName(d))))
            {
                CollectProjectFiles(subDir, projects, visited);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // A subtree we cannot read is skipped, not fatal — same rule batch discovery keeps.
        }
    }

    private static bool IsProjectFile(string path)
    {
        return Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj";
    }

    private void WriteCpmDetails(WorkspaceStatus status)
    {
        if (!status.CpmEnabled)
        {
            _console.Dim("  No Directory.Packages.props — run 'cpmigrate' to create one.");
            return;
        }

        if (status.CentralPackageCount is { } packageCount)
        {
            _console.Success($"  CPM active: {packageCount} package version(s) managed centrally.");
        }
        else
        {
            _console.Warning("  CPM file exists but could not be read.");
        }

        // NuGet evaluates only the nearest props file per project — when another file's directory
        // is an ancestor of this one's, the pin count above does not describe the projects beneath
        // the deeper file, and restore gives no diagnostic. Doctor carries the full check.
        if (status.ShadowedPropsFiles.Count > 0)
        {
            _console.Warning(
                $"  {status.ShadowedPropsFiles.Count} Directory.Packages.props files share one "
                + "ancestry — only the nearest applies per project. Run 'cpmigrate --doctor' "
                + "for details."
            );
        }
    }

    private void WriteConfigDetails(WorkspaceStatus status)
    {
        if (status.ConfigPresent)
        {
            _console.Dim("  Team config: .cpmigrate.json found.");
        }
        else
        {
            _console.Dim("  No .cpmigrate.json — run 'cpmigrate --init' to create one.");
        }
    }

    private void WriteQuickStats(WorkspaceStatus status)
    {
        if (status.ProjectCount > 0)
        {
            _console.Dim($"  {status.ProjectCount} project(s) across {status.Solutions.Count} solution(s).");
        }
    }

    private static List<BackupSetInfo> GetBackupInfo(string dir)
    {
        var backupDir = Path.Combine(dir, ".cpmigrate_backup");
        if (!Directory.Exists(backupDir))
        {
            return new List<BackupSetInfo>();
        }

        return Directory.GetDirectories(backupDir)
            .Select(d => new BackupSetInfo
            {
                Timestamp = Path.GetFileName(d),
                Files = Directory.GetFiles(d, "*", SearchOption.AllDirectories).ToList(),
            })
            .OrderByDescending(b => b.Timestamp)
            .ToList();
    }

    private static bool HasUnstagedChanges(string dir)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
#pragma warning disable S4036 // Suppress PATH warning: CLI tool intentionally uses git from PATH
                FileName = "git",
#pragma warning restore S4036
                Arguments = "status --porcelain",
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            using var capture = new ProcessOutputCapture();
            capture.BeginCapture(process);
            if (!capture.Wait(process, TimeSpan.FromSeconds(5)))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the timeout expiring and the kill.
                }

                return false;
            }

            return !string.IsNullOrWhiteSpace(capture.Output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Counts every framework a project targets — a multi-targeted project contributes to each of
    /// its TFMs, not just the first — across every project found, not only the top-level ones.
    /// </summary>
    private static Dictionary<string, int> DiscoverTargetFrameworks(List<string> projectFiles)
    {
        var result = new Dictionary<string, int>();

        foreach (var file in projectFiles)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch
            {
                // Best-effort; one unreadable project does not fail the whole status.
                continue;
            }

            foreach (var tfm in ExtractTargetFrameworks(content))
            {
                result.TryGetValue(tfm, out var count);
                result[tfm] = count + 1;
            }
        }

        return result;
    }

    private static IEnumerable<string> ExtractTargetFrameworks(string content)
    {
        var singleTag = "<TargetFramework>";
        var singleIdx = content.IndexOf(singleTag, StringComparison.Ordinal);
        if (singleIdx >= 0)
        {
            var start = singleIdx + singleTag.Length;
            var end = content.IndexOf("</TargetFramework>", start, StringComparison.Ordinal);
            if (end > start)
            {
                yield return content[start..end].Trim();
            }
        }

        var multiTag = "<TargetFrameworks>";
        var multiIdx = content.IndexOf(multiTag, StringComparison.Ordinal);
        if (multiIdx >= 0)
        {
            var start = multiIdx + multiTag.Length;
            var end = content.IndexOf("</TargetFrameworks>", start, StringComparison.Ordinal);
            if (end > start)
            {
                foreach (var tfm in content[start..end].Split(';'))
                {
                    var trimmed = tfm.Trim();
                    if (trimmed.Length > 0)
                    {
                        yield return trimmed;
                    }
                }
            }
        }
    }
}
