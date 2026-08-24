using System.Reflection;
using System.Runtime.InteropServices;
using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

internal sealed class DoctorService
{
#pragma warning disable S1075 // URIs should not be hardcoded - NuGet public API is a stable URL
    private const string NuGetServiceIndexUrl = "https://api.nuget.org/v3/index.json";
#pragma warning restore S1075

    private readonly IConsoleService _console;
    private readonly ISolutionDiscovery _solutionDiscovery;

    public DoctorService(IConsoleService console, ISolutionDiscovery solutionDiscovery)
    {
        _console = console;
        _solutionDiscovery = solutionDiscovery;
    }

    public async Task<int> RunAsync(string searchPath, string? backupDir = null)
    {
        var theme = SpectreTheme.For(AnsiConsole.Console);
        var checks = new List<DoctorCheck>();

        checks.Add(CheckDotNetSdk());
        checks.Add(CheckCpmigrateVersion());
        checks.Add(CheckRuntime());
        checks.Add(await CheckNuGetConnectivityAsync());
        checks.AddRange(CheckWorkspace(searchPath));
        checks.Add(CheckDiskSpace(searchPath));
        checks.Add(CheckWriteAccess(searchPath));
        checks.Add(CheckBackupDirAccess(searchPath, backupDir));
        checks.Add(CheckConfigFile(searchPath));
        checks.Add(CheckGitStatus(searchPath));

        RenderReport(theme, checks);

        var failures = checks.Count(c => c.Status == DoctorStatus.Error);
        var warnings = checks.Count(c => c.Status == DoctorStatus.Warning);

        _console.WriteLine();
        if (failures > 0)
        {
            _console.Error($"{failures} check(s) failed, {warnings} warning(s). Run 'cpmigrate --doctor' after fixing the errors above.");
            return ExitCodes.UnexpectedError;
        }

        if (warnings > 0)
        {
            _console.Warning($"{warnings} warning(s) — everything will work, but review the items above.");
        }
        else
        {
            _console.Success("All checks passed. Your environment is ready for CPMigrate.");
        }

        return ExitCodes.Success;
    }

    private static DoctorCheck CheckDotNetSdk()
    {
        try
        {
            var sdkVersion = Environment.Version.ToString();
            var major = Environment.Version.Major;

            if (major >= 10)
            {
                return new DoctorCheck("SDK", DoctorStatus.Ok, $".NET SDK {sdkVersion}");
            }

            if (major >= 8)
            {
                return new DoctorCheck("SDK", DoctorStatus.Ok, $".NET SDK {sdkVersion} (8.0+ supported)");
            }

            return new DoctorCheck("SDK", DoctorStatus.Error,
                $".NET SDK {sdkVersion} — CPMigrate requires 8.0 or later",
                "Install from https://dotnet.microsoft.com/download");
        }
        catch (Exception ex)
        {
            return new DoctorCheck("SDK", DoctorStatus.Error, $"Could not determine SDK version: {ex.Message}");
        }
    }

    private static DoctorCheck CheckCpmigrateVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        return new DoctorCheck("Tool", DoctorStatus.Ok, $"CPMigrate v{version}");
    }

    private static DoctorCheck CheckRuntime()
    {
        var runtime = RuntimeInformation.FrameworkDescription;
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.OSArchitecture.ToString();
        return new DoctorCheck("Runtime", DoctorStatus.Ok, $"{runtime} on {os} ({arch})");
    }

    private static async Task<DoctorCheck> CheckNuGetConnectivityAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync(NuGetServiceIndexUrl);

            if (response.IsSuccessStatusCode)
            {
                return new DoctorCheck("NuGet", DoctorStatus.Ok, "nuget.org reachable");
            }

            return new DoctorCheck("NuGet", DoctorStatus.Warning,
                $"nuget.org returned {(int)response.StatusCode}",
                "Package queries may fail. Check your network or proxy settings.");
        }
        catch (Exception)
        {
            return new DoctorCheck("NuGet", DoctorStatus.Warning,
                "Cannot reach nuget.org",
                "Package version lookups and --audit will fail. Check your network connection.");
        }
    }

    private List<DoctorCheck> CheckWorkspace(string searchPath)
    {
        var checks = new List<DoctorCheck>();
        var dir = Directory.Exists(searchPath) ? searchPath : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";

        var solutions = _solutionDiscovery.GetSolutionFiles(dir).ToList();
        if (solutions.Count > 0)
        {
            var names = string.Join(", ", solutions.Select(Path.GetFileName));
            checks.Add(new DoctorCheck("Solutions", DoctorStatus.Ok, $"{solutions.Count} solution(s): {names}"));
        }
        else
        {
            var projects = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(dir, "*.fsproj", SearchOption.TopDirectoryOnly))
                .Concat(Directory.GetFiles(dir, "*.vbproj", SearchOption.TopDirectoryOnly))
                .ToList();

            if (projects.Count > 0)
            {
                checks.Add(new DoctorCheck("Projects", DoctorStatus.Ok,
                    $"{projects.Count} project(s) found (no solution file)"));
            }
            else
            {
                checks.Add(new DoctorCheck("Solutions", DoctorStatus.Warning,
                    "No .sln, .slnx, or project files found here",
                    "Run from a directory containing a solution, or pass -s / -p."));
            }
        }

        var propsPath = Path.Combine(dir, "Directory.Packages.props");
        if (File.Exists(propsPath))
        {
            checks.Add(new DoctorCheck("CPM", DoctorStatus.Ok, "Directory.Packages.props found — CPM is active"));
        }
        else
        {
            checks.Add(new DoctorCheck("CPM", DoctorStatus.Info,
                "No Directory.Packages.props — run 'cpmigrate' to create one"));
        }

        return checks;
    }

    internal static DoctorCheck CheckDiskSpace(string searchPath)
    {
        var dir = Directory.Exists(searchPath) ? searchPath : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            var freeBytes = new DriveInfo(root!).AvailableFreeSpace;
            return ClassifyDiskSpace(freeBytes);
        }
        catch (Exception ex)
        {
            // Exotic filesystems and unready volumes can throw — degrade to Info rather than crash doctor.
            return new DoctorCheck("Disk", DoctorStatus.Info, $"Could not determine free disk space: {ex.Message}");
        }
    }

    internal static DoctorCheck ClassifyDiskSpace(long freeBytes)
    {
        const long ErrorThresholdBytes = 200L * 1024 * 1024;      // ~200 MB
        const long WarningThresholdBytes = 2L * 1024 * 1024 * 1024; // ~2 GB

        if (freeBytes < ErrorThresholdBytes)
        {
            return new DoctorCheck("Disk", DoctorStatus.Error,
                $"Only {FormatSize(freeBytes)} free on the workspace volume",
                "Backups (.cpmigrate_backup/) plus restore artifacts consume space during migration. Free up space first.");
        }

        if (freeBytes < WarningThresholdBytes)
        {
            return new DoctorCheck("Disk", DoctorStatus.Warning,
                $"{FormatSize(freeBytes)} free on the workspace volume",
                "Backups plus restore artifacts consume space during migration.");
        }

        return new DoctorCheck("Disk", DoctorStatus.Ok,
            $"{FormatSize(freeBytes)} free on the workspace volume");
    }

    private static string FormatSize(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F2} GB",
            >= MB => $"{bytes / (double)MB:F2} MB",
            >= KB => $"{bytes / (double)KB:F2} KB",
            _ => $"{bytes} bytes",
        };
    }

    internal static DoctorCheck CheckWriteAccess(string searchPath)
    {
        var dir = Directory.Exists(searchPath) ? searchPath : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";
        return ProbeWriteAccess(dir);
    }

    internal static DoctorCheck ProbeWriteAccess(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return new DoctorCheck("Write", DoctorStatus.Info,
                $"Cannot probe '{dir}' — directory does not exist");
        }

        try
        {
            var probePath = Path.Combine(dir, $".cpmigrate-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);

            try
            {
                File.Delete(probePath);
            }
            catch (Exception)
            {
                // Creating files but being denied Delete/DeleteChild is real: the atomic writer
                // replaces existing files, which needs deletion rights, so a migration would die
                // mid-run. A leftover probe is evidence of exactly that.
                return new DoctorCheck("Write", DoctorStatus.Warning,
                    $"Workspace accepts new files but the probe could not be deleted: {probePath}",
                    "Deletion rights are required — CPMigrate replaces files in place during "
                        + "migration. Delete the probe file and check directory ACLs.");
            }

            return new DoctorCheck("Write", DoctorStatus.Ok, "Workspace directory is writable");
        }
        catch (UnauthorizedAccessException)
        {
            return new DoctorCheck("Write", DoctorStatus.Error,
                "Workspace directory is not writable",
                "CPMigrate rewrites project files and writes backups during migration — it cannot run against a read-only workspace.");
        }
        catch (Exception ex)
        {
            return new DoctorCheck("Write", DoctorStatus.Info,
                $"Could not verify write access: {ex.Message}");
        }
    }

    /// <summary>
    /// Probes the configured backup directory when it is somewhere other than the workspace
    /// itself — a separate volume or a nested path can pass the workspace probe and still be
    /// unwritable, and backups failing mid-migration is exactly what doctor exists to prevent.
    /// </summary>
    internal static DoctorCheck CheckBackupDirAccess(string searchPath, string? backupDir)
    {
        if (string.IsNullOrWhiteSpace(backupDir))
        {
            return new DoctorCheck("Backup", DoctorStatus.Info, "No backup directory configured");
        }

        var workspace = Directory.Exists(searchPath)
            ? Path.GetFullPath(searchPath)
            : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";
        var resolved = Path.GetFullPath(
            Path.IsPathRooted(backupDir) ? backupDir : Path.Combine(workspace, backupDir)
        );

        // Case-sensitive where the filesystem is: /repo/App and /repo/app are different
        // directories on Linux, and only the real one is covered by the workspace probe.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            string.Equals(
                resolved.TrimEnd(Path.DirectorySeparatorChar),
                workspace.TrimEnd(Path.DirectorySeparatorChar),
                comparison
            )
        )
        {
            // Already covered by the workspace write probe; a second identical line says nothing.
            return new DoctorCheck("Backup", DoctorStatus.Info,
                "Backups go to the workspace directory (covered by the Write check)");
        }

        var check = ProbeWriteAccess(resolved);
        return check.Status == DoctorStatus.Ok
            ? new DoctorCheck("Backup", DoctorStatus.Ok, $"Backup directory '{resolved}' is writable")
            : new DoctorCheck("Backup", check.Status, check.Details, check.Hint);
    }

    private static DoctorCheck CheckConfigFile(string searchPath)
    {
        var dir = Directory.Exists(searchPath) ? searchPath : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";
        var configPath = Path.Combine(dir, ".cpmigrate.json");

        if (File.Exists(configPath))
        {
            return new DoctorCheck("Config", DoctorStatus.Ok, ".cpmigrate.json found");
        }

        return new DoctorCheck("Config", DoctorStatus.Info,
            "No .cpmigrate.json — using defaults (run 'cpmigrate --init' to create one)");
    }

    private static DoctorCheck CheckGitStatus(string searchPath)
    {
        var dir = Directory.Exists(searchPath) ? searchPath : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";
        var gitDir = Path.Combine(dir, ".git");

        if (Directory.Exists(gitDir))
        {
            var backupDir = Path.Combine(dir, ".cpmigrate_backup");
            if (Directory.Exists(backupDir))
            {
                var gitignore = Path.Combine(dir, ".gitignore");
                var ignored = File.Exists(gitignore) &&
                    File.ReadAllLines(gitignore).Any(l =>
                        l.Trim().Equals(".cpmigrate_backup", StringComparison.OrdinalIgnoreCase) ||
                        l.Trim().Equals(".cpmigrate_backup/", StringComparison.OrdinalIgnoreCase));

                if (!ignored)
                {
                    return new DoctorCheck("Git", DoctorStatus.Warning,
                        ".cpmigrate_backup/ exists but is not in .gitignore",
                        "Run with --add-gitignore or add '.cpmigrate_backup/' to .gitignore.");
                }
            }

            return new DoctorCheck("Git", DoctorStatus.Ok, "Git repository detected");
        }

        return new DoctorCheck("Git", DoctorStatus.Info, "Not a git repository");
    }

    private void RenderReport(SpectreTheme theme, List<DoctorCheck> checks)
    {
        _console.WriteHeader();
        _console.Banner("ENVIRONMENT DOCTOR");
        _console.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(SpectrePalette.CyberColors.Dim)
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]CHECK[/]"))
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]STATUS[/]"))
            .AddColumn(new TableColumn($"[bold {SpectrePalette.Ink.Text}]DETAILS[/]"));

        foreach (var check in checks)
        {
            var (icon, ink) = check.Status switch
            {
                DoctorStatus.Ok => (theme.Glyphs.Success, SpectrePalette.Ink.Success),
                DoctorStatus.Warning => (theme.Glyphs.Warning, SpectrePalette.Ink.Warning),
                DoctorStatus.Error => (theme.Glyphs.Error, SpectrePalette.Ink.Error),
                DoctorStatus.Info => (theme.Glyphs.Info, SpectrePalette.Ink.Dim),
                _ => (theme.Glyphs.Bullet, SpectrePalette.Ink.Dim),
            };

            var details = $"[{ink}]{SpectrePalette.Escape(check.Details)}[/]";
            if (check.Hint is not null)
            {
                details += $"\n[{SpectrePalette.Ink.Muted}]{SpectrePalette.Escape(check.Hint)}[/]";
            }

            table.AddRow(
                $"[{SpectrePalette.Ink.Text}]{SpectrePalette.Escape(check.Name)}[/]",
                $"[{ink}]{icon}[/]",
                details);
        }

        AnsiConsole.Write(table);
    }
}

internal enum DoctorStatus
{
    Ok,
    Info,
    Warning,
    Error,
}

internal sealed record DoctorCheck(
    string Name,
    DoctorStatus Status,
    string Details,
    string? Hint = null);
