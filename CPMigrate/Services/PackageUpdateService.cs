using CPMigrate.Models;
using CPMigrate.Services.Update;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates updating NuGet packages to latest versions with test verification and rollback.
/// </summary>
public sealed class PackageUpdateService : IPackageUpdateService
{
    private const string IncompleteScanGuidance =
        "A project that cannot restore against your configured feeds cannot be updated safely.";

    private readonly IConsoleService _consoleService;
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly PropsGenerator _propsGenerator;
    private readonly IUpdateCandidateSource _candidateSource;
    private readonly IDotNetCliService _dotNetCli;
    private readonly IBackupManager _backupManager;
    private readonly ILogger<PackageUpdateService> _logger;

    public PackageUpdateService(
        IConsoleService consoleService,
        IProjectAnalyzer projectAnalyzer,
        PropsGenerator propsGenerator,
        IUpdateCandidateSource candidateSource,
        IDotNetCliService dotNetCli,
        IBackupManager backupManager,
        ILogger<PackageUpdateService>? logger = null)
    {
        _consoleService = consoleService;
        _projectAnalyzer = projectAnalyzer;
        _propsGenerator = propsGenerator;
        _candidateSource = candidateSource;
        _dotNetCli = dotNetCli;
        _backupManager = backupManager;
        _logger = logger ?? NullLogger<PackageUpdateService>.Instance;
    }

    public Task<PackageUpdateResult> UpdatePackagesAsync(Options options)
    {
        return UpdatePackagesAsync(PackageUpdateRequest.FromOptions(options));
    }

    /// <inheritdoc />
    public async Task<PackageUpdateResult> UpdatePackagesAsync(PackageUpdateRequest request)
    {
        var load = await DiscoverAndLoadCurrentVersionsAsync(request);
        if (load.EarlyResult != null)
        {
            return load.EarlyResult;
        }

        _consoleService.Info($"Checking {load.CurrentVersions.Count} packages for updates...");
        var scan = await QueryAllUpdatesAsync(load.CurrentVersions, load.ProjectPaths, request);
        if (scan.UnscannedProjects.Count > 0)
        {
            return BuildIncompleteScanResult(load.CurrentVersions.Count, scan);
        }

        if (scan.TransitiveScanFailed)
        {
            _consoleService.Warning("Could not scan transitive dependencies. Continuing with direct updates only.");
        }

        var availableUpdates = ApplyOnlyFilter(FilterAvailableUpdates(scan.Updates.ToList()), request);
        if (availableUpdates.Count == 0)
        {
            // A package absent from every configured feed is not a candidate — that is the correct
            // answer, not an incomplete one. Incomplete is UnscannedProjects, handled above.
            _consoleService.Success("Everything up to date!");

            return BuildNoUpdatesResult(load.CurrentVersions.Count, scan.TransitivePackagesFound);
        }

        ShowUpdatesTable(availableUpdates);

        var acceptedUpdates = RunMajorVersionWizard(availableUpdates, request);
        var updatesToApply = acceptedUpdates.Where(u => u.Accepted).ToList();
        if (updatesToApply.Count == 0)
        {
            _consoleService.Info("No updates selected.");
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = load.CurrentVersions.Count,
                PackagesSkipped = load.CurrentVersions.Count,
                TransitivePackagesFound = scan.TransitivePackagesFound,
                Updates = acceptedUpdates
            };
        }

        if (request.DryRun)
        {
            return BuildDryRunResult(load.CurrentVersions.Count, scan.TransitivePackagesFound, updatesToApply, acceptedUpdates);
        }

        var backup = await CreateBackupAsync(request, load.PropsPath);
        if (backup.EarlyResult != null)
        {
            return backup.EarlyResult;
        }

        AnnounceApply(updatesToApply);

        var transaction = await PropsUpdateTransaction.BeginAsync(
            load.PropsPath, load.CurrentVersions, _propsGenerator);
        var runner = new DotNetVerificationRunner(
            _dotNetCli,
            _consoleService,
            FindSolutionFile(load.BasePath) ?? load.BasePath,
            request.BisectTestFilter);
        var strategy = CreateSearchStrategy(request);

        UpdateSearchResult search;
        try
        {
            search = await strategy.SearchAsync(updatesToApply, transaction, runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The props file may be half-written; get the user back to a known state before surfacing this.
            _consoleService.Error($"Update failed while writing Directory.Packages.props: {ex.Message}");
            return await RecoverFromWriteFailureAsync(
                transaction, backup.Path, backup.Manifest, load.CurrentVersions.Count, acceptedUpdates, scan.TransitivePackagesFound);
        }

        return FinalizeSearch(
            search, backup.Path, backup.Manifest, load.CurrentVersions.Count,
            acceptedUpdates, scan.TransitivePackagesFound, request);
    }

    private IUpdateSearchStrategy CreateSearchStrategy(PackageUpdateRequest request)
    {
        if (!request.Bisect)
        {
            return new AllOrNothingSearchStrategy();
        }

        _consoleService.Info(
            $"Bisect enabled: holding back only the updates that break verification (budget: {request.BisectBudget} run(s)).");
        return new BisectSearchStrategy(_consoleService, request.BisectBudget);
    }

    private async Task<UpdateLoadContext> DiscoverAndLoadCurrentVersionsAsync(PackageUpdateRequest request)
    {
        var solutionDir = Path.GetFullPath(request.SolutionPath);
        var (basePath, projectPaths) = await _projectAnalyzer.DiscoverProjectsFromSolutionAsync(solutionDir);

        var propsPath = FindPropsFile(basePath);
        if (propsPath == null)
        {
            _consoleService.Error("Directory.Packages.props not found. CPM is not enabled. Run 'cpmigrate' first.");
            return UpdateLoadContext.FromEarly(new PackageUpdateResult { ExitCode = ExitCodes.ValidationError });
        }

        var currentVersions = PropsGenerator.ReadExistingPackageVersions(propsPath, out _);
        if (currentVersions.Count == 0)
        {
            _consoleService.Info("No packages found in Directory.Packages.props.");
            return UpdateLoadContext.FromEarly(new PackageUpdateResult { ExitCode = ExitCodes.Success });
        }

        return new UpdateLoadContext(basePath, projectPaths, propsPath, currentVersions);
    }

    private Task<UpdateCandidateScan> QueryAllUpdatesAsync(
        Dictionary<string, HashSet<string>> currentVersions,
        List<string> projectPaths,
        PackageUpdateRequest request)
    {
        return _candidateSource.FindAsync(
            projectPaths,
            currentVersions,
            request.IncludeTransitive,
            request.IncludePrerelease,
            request.MaxParallelism);
    }

    private PackageUpdateResult BuildIncompleteScanResult(int packagesChecked, UpdateCandidateScan scan)
    {
        var names = string.Join(
            ", ",
            scan.UnscannedProjects.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var warning =
            $"Could not scan {scan.UnscannedProjects.Count} project(s) against the configured NuGet feeds: {names}. "
            + IncompleteScanGuidance;

        _logger.LogWarning(
            "Update candidate scan incomplete for {Count} project(s): {Projects}",
            scan.UnscannedProjects.Count,
            names);

        _consoleService.Error(
            $"Could not scan {scan.UnscannedProjects.Count} project(s) against the configured NuGet feeds: {names}");
        _consoleService.Dim(IncompleteScanGuidance);

        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.IncompleteAnalysis,
            PackagesChecked = packagesChecked,
            PackagesSkipped = packagesChecked,
            TransitivePackagesFound = scan.TransitivePackagesFound,
            Warnings = [warning]
        };
    }

    /// <summary>
    /// Narrows the candidate set to the packages named by <c>--only</c>. Warns about names that matched
    /// nothing so a typo does not look like "already up to date".
    /// </summary>
    private List<PackageUpdateEntry> ApplyOnlyFilter(List<PackageUpdateEntry> updates, PackageUpdateRequest request)
    {
        if (request.OnlyPackages is not { Count: > 0 } only)
        {
            return updates;
        }

        var wanted = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
        var filtered = updates.Where(u => wanted.Contains(u.PackageName)).ToList();

        var unmatched = only
            .Where(name => !updates.Any(u => string.Equals(u.PackageName, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unmatched.Count > 0)
        {
            _consoleService.Warning(
                $"--only matched no available update for: {string.Join(", ", unmatched)}");
        }

        return filtered;
    }

    private static List<PackageUpdateEntry> FilterAvailableUpdates(List<PackageUpdateEntry> updates)
    {
        return updates
            .Where(u =>
            {
                var current = NuGetVersion.TryParse(u.CurrentVersion, out var c) ? c : null;
                var latest = NuGetVersion.TryParse(u.LatestVersion, out var l) ? l : null;
                return current != null && latest != null && latest > current;
            })
            .ToList();
    }

    private static PackageUpdateResult BuildNoUpdatesResult(int packagesChecked, int transitiveFound)
    {
        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.Success,
            PackagesChecked = packagesChecked,
            PackagesSkipped = packagesChecked,
            TransitivePackagesFound = transitiveFound
        };
    }

    private PackageUpdateResult BuildDryRunResult(
        int packagesChecked, int transitiveFound,
        List<PackageUpdateEntry> updatesToApply, List<PackageUpdateEntry> acceptedUpdates)
    {
        var directDryRun = updatesToApply.Where(u => !u.IsTransitive).ToList();
        var transitiveDryRun = updatesToApply.Where(u => u.IsTransitive).ToList();
        _consoleService.DryRun($"Would update {directDryRun.Count} direct package(s)" +
            (transitiveDryRun.Count > 0 ? $" and pin {transitiveDryRun.Count} transitive package(s)." : "."));
        ShowDryRunSummary(updatesToApply);
        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.Success,
            PackagesChecked = packagesChecked,
            PackagesUpdated = directDryRun.Count,
            PackagesSkipped = packagesChecked - directDryRun.Count,
            TransitivePackagesFound = transitiveFound,
            TransitivePackagesUpdated = transitiveDryRun.Count,
            Updates = acceptedUpdates
        };
    }

    private async Task<UpdateBackupContext> CreateBackupAsync(PackageUpdateRequest request, string propsPath)
    {
        string backupPath;
        try
        {
            backupPath = BackupManager.CreateBackupDirectory(request.Backup);
        }
        catch (IOException ex)
        {
            _consoleService.Error($"Failed to create backup directory: {ex.Message}");
            return UpdateBackupContext.FromEarly(new PackageUpdateResult { ExitCode = ExitCodes.FileOperationError });
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupEntry = _backupManager.CreateBackupForProject(request.Backup, propsPath, backupPath, timestamp);
        var manifest = new BackupManifest
        {
            Timestamp = timestamp,
            PropsFilePath = propsPath,
            PropsFileExisted = true,
            Backups = backupEntry != null ? [backupEntry] : []
        };
        await BackupManager.WriteManifestAsync(backupPath, manifest);

        return new UpdateBackupContext(backupPath, manifest);
    }

    private const string StaleAssetsGuidance =
        "obj/project.assets.json still describes the rejected update until the next "
        + "dotnet restore — restore before building or trusting external tools.";

    private void AnnounceApply(List<PackageUpdateEntry> updatesToApply)
    {
        var directCount = updatesToApply.Count(u => !u.IsTransitive);
        var transitiveCount = updatesToApply.Count(u => u.IsTransitive);
        var applyMsg = $"Applying {directCount} direct update(s)";
        if (transitiveCount > 0)
        {
            applyMsg += $" and pinning {transitiveCount} transitive package(s)";
        }
        _consoleService.Info(applyMsg + "...");
    }

    /// <summary>
    /// Turns the search outcome into a user-facing report, a cleaned-up backup directory, and a result object.
    /// The props file is already in its final state by the time this runs — the strategy guarantees that.
    /// </summary>
    private PackageUpdateResult FinalizeSearch(
        UpdateSearchResult search,
        string backupPath,
        BackupManifest manifest,
        int packagesChecked,
        List<PackageUpdateEntry> acceptedUpdates,
        int transitiveFound,
        PackageUpdateRequest request)
    {
        ReportSearchOutcome(search, request);
        BackupManager.CleanupBackups(backupPath, manifest);

        var directApplied = search.Applied.Count(u => !u.IsTransitive);
        var transitiveApplied = search.Applied.Count(u => u.IsTransitive);
        var heldByName = search.HeldBack.ToDictionary(h => h.PackageName, StringComparer.OrdinalIgnoreCase);

        // Reflect hold-backs onto the reported entries so JSON consumers can see exactly what was excluded.
        var reportedUpdates = acceptedUpdates
            .Select(u => heldByName.ContainsKey(u.PackageName) ? u with { HeldBack = true } : u)
            .ToList();

        // Mirror what the console was told: a rejected update leaves obj/ stale until the next
        // restore, and held-back bisect probes were reverted without one.
        IReadOnlyList<string>? warnings =
            (!request.Bisect && !search.AnyApplied)
            || (request.Bisect && search.HeldBack.Count > 0)
                ? [StaleAssetsGuidance]
                : null;

        return new PackageUpdateResult
        {
            ExitCode = search.AnyApplied ? ExitCodes.Success : ExitCodes.TestFailure,
            Warnings = warnings,
            PackagesChecked = packagesChecked,
            PackagesUpdated = directApplied,
            PackagesSkipped = packagesChecked - directApplied,
            TransitivePackagesFound = transitiveFound,
            TransitivePackagesUpdated = transitiveApplied,
            PackagesHeldBack = search.HeldBack.Count,
            VerificationRuns = search.VerificationRuns,
            BisectBudgetExhausted = search.BudgetExhausted,
            TestsPassed = search.AnyApplied,
            WasRolledBack = !search.AnyApplied,
            Updates = reportedUpdates
        };
    }

    private void ReportSearchOutcome(UpdateSearchResult search, PackageUpdateRequest request)
    {
        if (search.AllApplied)
        {
            var directApplied = search.Applied.Count(u => !u.IsTransitive);
            var transitiveApplied = search.Applied.Count(u => u.IsTransitive);
            var successMsg = $"All tests passed! Updated {directApplied} package(s)";
            if (transitiveApplied > 0)
            {
                successMsg += $" and pinned {transitiveApplied} transitive package(s)";
            }
            _consoleService.Success(successMsg + ".");
            return;
        }

        if (!request.Bisect)
        {
            // Say which step actually failed: when restore falls over, the tests never ran at all.
            var cause = search.FailureOutcome == VerificationOutcome.RestoreFailed
                ? "dotnet restore failed!"
                : "Tests failed!";
            _consoleService.Error($"{cause} Rolled back to previous versions.");
            _consoleService.Dim(StaleAssetsGuidance);
            _consoleService.Dim(search.FailureOutput ?? string.Empty);
            return;
        }

        WriteBisectReport(search);
    }

    private void WriteBisectReport(UpdateSearchResult search)
    {
        _consoleService.WriteLine();
        _consoleService.Banner("BISECT RESULT");
        _consoleService.WriteLine();

        foreach (var held in search.HeldBack.OrderBy(h => h.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            _consoleService.Warning(
                $"  HELD    {held.PackageName}: {held.CurrentVersion} → {held.LatestVersion} (left at {held.CurrentVersion})");
        }

        foreach (var applied in search.Applied.OrderBy(a => a.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            _consoleService.Dim($"  APPLIED {applied.PackageName}: {applied.CurrentVersion} → {applied.LatestVersion}");
        }

        _consoleService.WriteLine();

        // Every held-back package was probed and reverted without a following restore, so obj/
        // describes whichever probe ran last — not the kept subset.
        if (search.HeldBack.Count > 0)
        {
            _consoleService.Dim(StaleAssetsGuidance);
        }

        var total = search.Applied.Count + search.HeldBack.Count;
        if (search.AnyApplied)
        {
            _consoleService.Success(
                $"Kept {search.Applied.Count}/{total} update(s) with tests green " +
                $"({search.VerificationRuns} verification run(s)).");
        }
        else if (search.BaselineBroken)
        {
            _consoleService.Error(
                "No updates applied: verification fails even with zero updates, so no package could be cleared. " +
                "Fix the existing failures first.");
        }
        else
        {
            _consoleService.Error(
                $"No updates could be kept — all {total} broke verification " +
                $"({search.VerificationRuns} verification run(s)).");
        }

        if (search.BudgetExhausted)
        {
            _consoleService.Warning(
                $"Bisect budget of {search.VerificationRuns} run(s) was exhausted before the search finished. " +
                "Unresolved updates were held back. Re-run with a higher --bisect-budget to narrow further.");
        }

        if (search.HeldBack.Count > 0)
        {
            var names = string.Join(",", search.HeldBack.Select(h => h.PackageName).Order(StringComparer.OrdinalIgnoreCase));
            _consoleService.Dim($"  Investigate with: cpmigrate --update-packages --only {names}");
        }
    }

    /// <summary>
    /// Best-effort recovery when the props file could not be written mid-search. Prefers the in-memory
    /// baseline, then the on-disk backup, and finally tells the user where to recover from by hand.
    /// </summary>
    private async Task<PackageUpdateResult> RecoverFromWriteFailureAsync(
        IUpdateTransaction transaction,
        string backupPath,
        BackupManifest manifest,
        int packagesChecked,
        List<PackageUpdateEntry> acceptedUpdates,
        int transitiveFound)
    {
        try
        {
            await transaction.RevertAsync();
            _consoleService.Success("Rolled back to previous versions.");
            _consoleService.Dim(StaleAssetsGuidance);
            BackupManager.CleanupBackups(backupPath, manifest);

            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.FileOperationError,
                PackagesChecked = packagesChecked,
                TransitivePackagesFound = transitiveFound,
                TestsPassed = false,
                WasRolledBack = true,
                Warnings = [StaleAssetsGuidance],
                Updates = acceptedUpdates
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _consoleService.Error($"Rollback failed: {ex.Message}");
            _consoleService.Warning($"Manual recovery: restore from backup at {backupPath}");

            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.FileOperationError,
                PackagesChecked = packagesChecked,
                TransitivePackagesFound = transitiveFound,
                TestsPassed = false,
                WasRolledBack = false,
                Updates = acceptedUpdates
            };
        }
    }

    private static string? FindPropsFile(string basePath)
    {
        var propsPath = Path.Combine(basePath, "Directory.Packages.props");
        return File.Exists(propsPath) ? propsPath : null;
    }

    private static string? FindSolutionFile(string basePath)
    {
        var slnFiles = Directory.GetFiles(basePath, "*.sln");
        return slnFiles.Length > 0 ? slnFiles[0] : null;
    }

    private void ShowUpdatesTable(List<PackageUpdateEntry> updates)
    {
        var directUpdates = updates.Where(u => !u.IsTransitive).OrderBy(u => u.PackageName).ToList();
        var transitiveUpdates = updates.Where(u => u.IsTransitive).OrderBy(u => u.PackageName).ToList();

        if (directUpdates.Count > 0)
        {
            _consoleService.WriteLine();
            _consoleService.Banner("DIRECT UPDATES");
            _consoleService.WriteLine();

            foreach (var update in directUpdates)
            {
                var label = update.IsMajorUpdate ? " (MAJOR)" : "";
                _consoleService.Info($"  {update.PackageName}: {update.CurrentVersion} → {update.LatestVersion}{label}");
            }

            _consoleService.WriteLine();
        }

        if (transitiveUpdates.Count > 0)
        {
            _consoleService.WriteLine();
            _consoleService.Banner("TRANSITIVE UPDATES");
            _consoleService.WriteLine();

            foreach (var update in transitiveUpdates)
            {
                var label = update.IsMajorUpdate ? " (MAJOR)" : "";
                _consoleService.Info($"  {update.PackageName}: {update.CurrentVersion} → {update.LatestVersion}{label}");
            }

            _consoleService.WriteLine();
        }
    }

    private List<PackageUpdateEntry> RunMajorVersionWizard(List<PackageUpdateEntry> updates, PackageUpdateRequest request)
    {
        var result = new List<PackageUpdateEntry>();
        // A redirected stdout cannot service the major-version prompt either, so it counts as
        // non-interactive alongside --quiet and --output Json: major updates are skipped, not
        // silently accepted.
        var nonInteractive = request.Output.IsNonInteractive || !_consoleService.IsInteractive;

        foreach (var update in updates)
        {
            if (!update.IsMajorUpdate)
            {
                result.Add(update with { Accepted = true });
                continue;
            }

            if (nonInteractive)
            {
                result.Add(update with { Accepted = false });
                continue;
            }

            var choices = new[]
            {
                $"Accept major update to {update.LatestVersion}",
                "Skip this package"
            };

            _consoleService.Warning($"{update.PackageName}: {update.CurrentVersion} → {update.LatestVersion} (MAJOR VERSION CHANGE)");
            var selection = _consoleService.AskSelection(
                $"How would you like to handle {update.PackageName}?",
                choices);

            var accepted = selection == choices[0];
            result.Add(update with { Accepted = accepted });
        }

        return result;
    }

    private void ShowDryRunSummary(List<PackageUpdateEntry> updates)
    {
        _consoleService.WriteLine();
        foreach (var update in updates.OrderBy(u => u.PackageName))
        {
            _consoleService.DryRun($"  {update.PackageName}: {update.CurrentVersion} → {update.LatestVersion}");
        }
        _consoleService.WriteLine();
    }

}

internal sealed record UpdateLoadContext(
    string BasePath,
    List<string> ProjectPaths,
    string PropsPath,
    Dictionary<string, HashSet<string>> CurrentVersions)
{
    public PackageUpdateResult? EarlyResult { get; private init; }

    public static UpdateLoadContext FromEarly(PackageUpdateResult result) =>
        new(string.Empty, new(), string.Empty, new()) { EarlyResult = result };
}

internal sealed record UpdateBackupContext(string Path, BackupManifest Manifest)
{
    public PackageUpdateResult? EarlyResult { get; private init; }

    public static UpdateBackupContext FromEarly(PackageUpdateResult result) =>
        new(string.Empty, null!) { EarlyResult = result };
}
