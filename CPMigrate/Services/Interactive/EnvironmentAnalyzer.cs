using CPMigrate.Models;

namespace CPMigrate.Services.Interactive;

/// <summary>
/// Analyzes the current directory environment for CPM migration readiness.
/// </summary>
internal class EnvironmentAnalyzer
{
    private readonly IConsoleService _console;

    public EnvironmentAnalyzer(IConsoleService console)
    {
        _console = console;
    }

    /// <summary>
    /// Analyzes the current directory for solutions, projects, git status, and potential conflicts.
    /// </summary>
    public EnvironmentContext Analyze()
    {
        var ctx = new EnvironmentContext { Directory = Directory.GetCurrentDirectory() };

        // Discover solutions
        ctx.Solutions = ProjectAnalyzer.GetSolutionFiles(ctx.Directory).ToList();
        ctx.IsCpm = File.Exists(Path.Combine(ctx.Directory, "Directory.Packages.props"));

        // Check backups
        var backupManager = new BackupManager();
        ctx.Backups = backupManager.GetBackupHistory(Path.Combine(ctx.Directory, ".cpmigrate_backup"));

        // Analyze git status
        AnalyzeGitStatus(ctx);

        // Deep scan for risk assessment
        AnalyzeProjectsAndConflicts(ctx);

        return ctx;
    }

    /// <summary>
    /// Checks git repository status.
    /// </summary>
    private void AnalyzeGitStatus(EnvironmentContext ctx)
    {
        ctx.IsGitRepo = Directory.Exists(Path.Combine(ctx.Directory, ".git"));

        if (!ctx.IsGitRepo)
        {
            return;
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.Arguments = "status --porcelain";
            process.StartInfo.WorkingDirectory = ctx.Directory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            ctx.HasUnstaged = !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            // Git not available - silently continue
        }
    }

    /// <summary>
    /// Scans projects and detects potential version conflicts.
    /// </summary>
    private void AnalyzeProjectsAndConflicts(EnvironmentContext ctx)
    {
        var analyzer = new ProjectAnalyzer(_console);
        var (basePath, projects) = analyzer.DiscoverProjectsFromSolution(ctx.Directory);

        if (projects.Count == 0)
        {
            (basePath, projects) = analyzer.DiscoverProjectFromPath(ctx.Directory);
        }

        ctx.ProjectCount = projects.Count;

        if (projects.Count == 0)
        {
            return;
        }

        var packages = new Dictionary<string, HashSet<string>>();

        foreach (var project in projects)
        {
            analyzer.ScanProjectPackages(project, packages);

            var targetFrameworks = analyzer.GetTargetFramework(project);
            AccumulateTargetFrameworks(ctx, targetFrameworks);
        }

        ctx.ConflictCount = VersionResolver.DetectConflicts(packages).Count;
    }

    /// <summary>
    /// Accumulates target framework statistics.
    /// </summary>
    private static void AccumulateTargetFrameworks(EnvironmentContext ctx, string targetFrameworks)
    {
        foreach (var tf in targetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ctx.TargetFrameworks.ContainsKey(tf))
            {
                ctx.TargetFrameworks[tf] = 0;
            }

            ctx.TargetFrameworks[tf]++;
        }
    }
}

/// <summary>
/// Contains environment analysis results.
/// </summary>
public class EnvironmentContext
{
    public string Directory { get; set; } = "";
    public List<string> Solutions { get; set; } = new();
    public List<BackupSetInfo> Backups { get; set; } = new();
    public bool IsGitRepo { get; set; }
    public bool HasUnstaged { get; set; }
    public bool IsCpm { get; set; }
    public int ProjectCount { get; set; }
    public int ConflictCount { get; set; }

    /// <summary>
    /// Reserved for future vulnerability analysis feature.
    /// </summary>
    public int VulnerabilityCount { get; set; }

    public Dictionary<string, int> TargetFrameworks { get; set; } = new();
}
