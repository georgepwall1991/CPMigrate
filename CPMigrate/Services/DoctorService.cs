using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CPMigrate.Analyzers;
using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

internal sealed class DoctorService
{
#pragma warning disable S1075 // URIs should not be hardcoded - NuGet public API is a stable URL
    private const string NuGetServiceIndexUrl = "https://api.nuget.org/v3/index.json";
#pragma warning restore S1075

    private static readonly Regex DetailedSourceLine = new(
        @"^\s*\d+\.\s+(?<name>.+?)\s+\[(?<state>Enabled|Disabled)\]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1)
    );

    private readonly IConsoleService _console;
    private readonly ISolutionDiscovery _solutionDiscovery;
    private readonly IDotNetCliService _dotNetCli;
    private readonly HttpClient? _httpClient;

    public DoctorService(
        IConsoleService console,
        ISolutionDiscovery solutionDiscovery,
        IDotNetCliService? dotNetCli = null,
        HttpClient? httpClient = null)
    {
        _console = console;
        _solutionDiscovery = solutionDiscovery;
        _dotNetCli = dotNetCli ?? new DotNetCliService();
        _httpClient = httpClient;
    }

    public async Task<int> RunAsync(string searchPath, string? backupDir = null)
    {
        var theme = SpectreTheme.For(AnsiConsole.Console);
        var checks = new List<DoctorCheck>();

        // The feed probe starts first and is awaited last, so doctor costs HTTP latency in
        // total — not HTTP latency plus every local check — while the report keeps the probe's
        // established row position.
        var nuGetTask = CheckNuGetSourcesAsync(searchPath);

        checks.Add(CheckDotNetSdk());
        checks.Add(CheckCpmigrateVersion());
        checks.Add(CheckRuntime());
        checks.AddRange(CheckWorkspace(searchPath));
        checks.Add(CheckDiskSpace(searchPath));
        checks.Add(CheckWriteAccess(searchPath));
        checks.Add(CheckBackupDirAccess(searchPath, backupDir));
        checks.Add(CheckConfigFile(searchPath));
        checks.Add(CheckGitStatus(searchPath));
        checks.Insert(3, await nuGetTask);

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

    private async Task<DoctorCheck> CheckNuGetSourcesAsync(string searchPath)
    {
        HttpClient? owned = null;
        HttpClient client;
        if (_httpClient is null)
        {
            owned = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client = owned;
        }
        else
        {
            client = _httpClient;
        }

        try
        {
            var workingDirectory = Directory.Exists(searchPath)
                ? searchPath
                : Path.GetDirectoryName(Path.GetFullPath(searchPath)) ?? ".";

            var (output, success) = await _dotNetCli.RunNugetListSourceAsync(workingDirectory);
            var sources = success ? ParseNugetListSource(output) : [];
            if (sources.Count == 0)
            {
                return await ProbeNugetOrgFallbackAsync(client);
            }

            var probes = new List<(NugetSource Source, int? StatusCode)>();
            foreach (var source in sources.Where(s => s.Enabled))
            {
                if (!source.IsHttp)
                {
                    probes.Add((source, StatusCode: null));
                    continue;
                }

                probes.Add((source, await ProbeHttpSourceAsync(client, source.Location)));
            }

            return SummarizeNugetSources(probes);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static async Task<int?> ProbeHttpSourceAsync(HttpClient client, string location)
    {
        try
        {
            using var response = await client.GetAsync(location);
            return (int)response.StatusCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<DoctorCheck> ProbeNugetOrgFallbackAsync(HttpClient client)
    {
        try
        {
            using var response = await client.GetAsync(NuGetServiceIndexUrl);
            if (response.IsSuccessStatusCode)
            {
                return new DoctorCheck(
                    "NuGet",
                    DoctorStatus.Ok,
                    "nuget.org reachable (could not list configured sources)",
                    "Run 'dotnet nuget list source' in the workspace to confirm private feeds."
                );
            }

            return new DoctorCheck(
                "NuGet",
                DoctorStatus.Warning,
                $"nuget.org returned {(int)response.StatusCode}",
                "Could not list configured sources, and the public feed is not healthy either."
            );
        }
        catch (Exception)
        {
            return new DoctorCheck(
                "NuGet",
                DoctorStatus.Warning,
                "Cannot list configured NuGet sources or reach nuget.org",
                "Package version lookups and --audit will fail. Check your network connection."
            );
        }
    }

    /// <summary>
    /// Parses <c>dotnet nuget list source --format Detailed</c> (and the Short shape as a fallback)
    /// into named sources. Disabled entries stay in the list so a test can see them; the probe
    /// skips them.
    /// </summary>
    internal static IReadOnlyList<NugetSource> ParseNugetListSource(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var sources = new List<NugetSource>();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var detailed = DetailedSourceLine.Match(line);
            if (detailed.Success)
            {
                var location = ReadIndentedLocation(lines, i + 1);
                sources.Add(
                    new NugetSource(
                        detailed.Groups["name"].Value.Trim(),
                        location,
                        Enabled: detailed.Groups["state"].Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                    )
                );
                continue;
            }

            // Short format: "nuget.org [Enabled]" with no following URL. Keep the name so the
            // doctor can still say which source exists; probing falls back to nuget.org only.
            var trimmed = line.Trim();
            if (trimmed.EndsWith("[Enabled]", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("[Disabled]", StringComparison.OrdinalIgnoreCase))
            {
                var enabled = trimmed.EndsWith("[Enabled]", StringComparison.OrdinalIgnoreCase);
                var name = trimmed[..trimmed.LastIndexOf('[')].Trim();
                if (name.Length > 0
                    && !name.Equals("Registered Sources:", StringComparison.OrdinalIgnoreCase)
                    && sources.TrueForAll(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    sources.Add(new NugetSource(name, Location: string.Empty, enabled));
                }
            }
        }

        return sources;
    }

    private static string ReadIndentedLocation(string[] lines, int start)
    {
        for (var i = start; i < lines.Length; i++)
        {
            var candidate = lines[i];
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (DetailedSourceLine.IsMatch(candidate))
            {
                return string.Empty;
            }

            return candidate.Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// Turns per-source probe results into the single doctor row. 200 is reachable; 401/403 is
    /// reachable-but-authenticated (a private feed answering, not a down feed); anything else
    /// names the source so the warning is actionable.
    /// </summary>
    internal static DoctorCheck SummarizeNugetSources(IReadOnlyList<(NugetSource Source, int? StatusCode)> probes)
    {
        if (probes.Count == 0)
        {
            return new DoctorCheck(
                "NuGet",
                DoctorStatus.Warning,
                "No enabled NuGet sources",
                "Add a source with 'dotnet nuget add source', or check nuget.config."
            );
        }

        var reachable = new List<string>();
        var problems = new List<string>();

        foreach (var (source, status) in probes)
        {
            if (!source.IsHttp)
            {
                reachable.Add($"{source.Name} (local)");
                continue;
            }

            if (status is (int)HttpStatusCode.OK)
            {
                reachable.Add(source.Name);
                continue;
            }

            if (status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
            {
                reachable.Add($"{source.Name} (authenticated)");
                continue;
            }

            problems.Add(
                status is null
                    ? $"{source.Name} unreachable"
                    : $"{source.Name} returned {status}"
            );
        }

        if (problems.Count == 0)
        {
            return new DoctorCheck(
                "NuGet",
                DoctorStatus.Ok,
                $"{reachable.Count} source(s) reachable: {string.Join(", ", reachable)}"
            );
        }

        var detail = string.Join("; ", problems);
        if (reachable.Count > 0)
        {
            detail += $"; reachable: {string.Join(", ", reachable)}";
        }

        return new DoctorCheck(
            "NuGet",
            DoctorStatus.Warning,
            detail,
            "Package updates and --outdated use these feeds. Fix the named source or your credential provider."
        );
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

        // The paths are the same directory only when their ancestry matches exactly and their
        // final component differs by case the parent's filesystem folds. Case sensitivity is a
        // property of the directory an entry lives in, so that last decision belongs to the
        // parent — probed with the same create-and-stat probe the drift analyzer uses. Anything
        // else treats the paths as different, which only means the backup probe runs: always
        // safe, never a skipped check.
        var resolvedTrimmed = resolved.TrimEnd(Path.DirectorySeparatorChar);
        var workspaceTrimmed = workspace.TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(resolvedTrimmed, workspaceTrimmed, StringComparison.Ordinal))
        {
            return new DoctorCheck("Backup", DoctorStatus.Info,
                "Backups go to the workspace directory (covered by the Write check)");
        }

        var parent = Path.GetDirectoryName(resolvedTrimmed);
        var sameAncestry = string.Equals(
            parent,
            Path.GetDirectoryName(workspaceTrimmed),
            StringComparison.Ordinal
        );
        var finalNameFolds =
            sameAncestry
            && string.Equals(
                Path.GetFileName(resolvedTrimmed),
                Path.GetFileName(workspaceTrimmed),
                StringComparison.OrdinalIgnoreCase
            )
            && !string.IsNullOrEmpty(parent)
            && Directory.Exists(parent)
            && CpmDriftAnalyzer.PathComparerFor(parent) == StringComparer.OrdinalIgnoreCase;

        if (sameAncestry && finalNameFolds)
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

/// <summary>
/// One entry from <c>dotnet nuget list source</c>.
/// </summary>
/// <param name="Name">The source name as registered (nuget.org, Contoso, …).</param>
/// <param name="Location">URL or local path. Empty when the Short listing omitted it.</param>
/// <param name="Enabled">Whether the source is enabled.</param>
internal readonly record struct NugetSource(string Name, string Location, bool Enabled)
{
    public bool IsHttp =>
        Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
