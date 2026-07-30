using CPMigrate.Models;

namespace CPMigrate.Services;

internal sealed class StatusService
{
    private readonly IConsoleService _console;
    private readonly ISolutionDiscovery _solutionDiscovery;

    public StatusService(IConsoleService console, ISolutionDiscovery solutionDiscovery)
    {
        _console = console;
        _solutionDiscovery = solutionDiscovery;
    }

    public Task<int> RunAsync(string searchPath)
    {
        var dir = Directory.Exists(searchPath) ? searchPath : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";

        _console.WriteHeader();
        _console.Banner("WORKSPACE STATUS");
        _console.WriteLine();

        var solutions = _solutionDiscovery.GetSolutionFiles(dir).ToList();
        var backups = GetBackupInfo(dir);
        var isGitRepo = Directory.Exists(Path.Combine(dir, ".git"));
        var hasUnstaged = isGitRepo && HasUnstagedChanges(dir);
        var targetFrameworks = DiscoverTargetFrameworks(dir);

        _console.WriteStatusDashboard(dir, solutions, backups, isGitRepo, hasUnstaged, targetFrameworks);

        WriteCpmDetails(dir);
        WriteConfigDetails(dir);
        WriteQuickStats(dir, solutions);

        _console.WriteLine();
        return Task.FromResult(ExitCodes.Success);
    }

    private void WriteCpmDetails(string dir)
    {
        var propsPath = Path.Combine(dir, "Directory.Packages.props");
        if (!File.Exists(propsPath))
        {
            _console.Dim("  No Directory.Packages.props — run 'cpmigrate' to create one.");
            return;
        }

        try
        {
            var content = File.ReadAllText(propsPath);
            var packageCount = content.Split("<PackageVersion", StringSplitOptions.None).Length - 1;
            _console.Success($"  CPM active: {packageCount} package version(s) managed centrally.");
        }
        catch
        {
            _console.Warning("  CPM file exists but could not be read.");
        }
    }

    private void WriteConfigDetails(string dir)
    {
        var configPath = Path.Combine(dir, ".cpmigrate.json");
        if (File.Exists(configPath))
        {
            _console.Dim("  Team config: .cpmigrate.json found.");
        }
        else
        {
            _console.Dim("  No .cpmigrate.json — run 'cpmigrate --init' to create one.");
        }
    }

    private void WriteQuickStats(string dir, List<string> solutions)
    {
        var projectFiles = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(dir, "*.vbproj", SearchOption.AllDirectories))
                .Where(p => !p.Contains(Path.Combine("bin", "")) && !p.Contains(Path.Combine("obj", "")))
                .ToList()
            : new List<string>();

        if (projectFiles.Count > 0)
        {
            _console.Dim($"  {projectFiles.Count} project(s) across {solutions.Count} solution(s).");
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
            var output = process?.StandardOutput.ReadToEnd() ?? string.Empty;
            process?.WaitForExit(5000);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, int> DiscoverTargetFrameworks(string dir)
    {
        var result = new Dictionary<string, int>();

        try
        {
            var projectFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(dir, "*.fsproj", SearchOption.TopDirectoryOnly))
                .Concat(Directory.GetFiles(dir, "*.vbproj", SearchOption.TopDirectoryOnly));

            foreach (var file in projectFiles)
            {
                var content = File.ReadAllText(file);
                var tfm = ExtractTargetFramework(content);
                if (tfm is not null)
                {
                    result.TryGetValue(tfm, out var count);
                    result[tfm] = count + 1;
                }
            }
        }
        catch
        {
            // Best-effort; don't fail the whole status on a read error.
        }

        return result;
    }

    private static string? ExtractTargetFramework(string content)
    {
        var singleTag = "<TargetFramework>";
        var singleIdx = content.IndexOf(singleTag, StringComparison.Ordinal);
        if (singleIdx >= 0)
        {
            var start = singleIdx + singleTag.Length;
            var end = content.IndexOf("</TargetFramework>", start, StringComparison.Ordinal);
            if (end > start)
            {
                return content[start..end].Trim();
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
                return content[start..end].Trim().Split(';').FirstOrDefault()?.Trim();
            }
        }

        return null;
    }
}
