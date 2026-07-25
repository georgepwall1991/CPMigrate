using CPMigrate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates updating NuGet packages to latest versions with test verification and rollback.
/// </summary>
public sealed class PackageUpdateService : IPackageUpdateService, IDisposable
{
    private readonly IConsoleService _consoleService;
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly PropsGenerator _propsGenerator;
    private readonly INuGetVersionLookupService _nuGetLookup;
    private readonly IDotNetCliService _dotNetCli;
    private readonly IBackupManager _backupManager;
    private readonly ILogger<PackageUpdateService> _logger;

    public PackageUpdateService(
        IConsoleService consoleService,
        IProjectAnalyzer projectAnalyzer,
        PropsGenerator propsGenerator,
        INuGetVersionLookupService nuGetLookup,
        IDotNetCliService dotNetCli,
        IBackupManager backupManager,
        ILogger<PackageUpdateService>? logger = null)
    {
        _consoleService = consoleService;
        _projectAnalyzer = projectAnalyzer;
        _propsGenerator = propsGenerator;
        _nuGetLookup = nuGetLookup;
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
        var (updates, transitiveFound) = await QueryAllUpdatesAsync(load.CurrentVersions, load.ProjectPaths, request);

        var availableUpdates = FilterAvailableUpdates(updates);
        if (availableUpdates.Count == 0)
        {
            _consoleService.Success("Everything up to date!");
            return BuildNoUpdatesResult(load.CurrentVersions.Count, transitiveFound);
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
                TransitivePackagesFound = transitiveFound,
                Updates = acceptedUpdates
            };
        }

        if (request.DryRun)
        {
            return BuildDryRunResult(load.CurrentVersions.Count, transitiveFound, updatesToApply, acceptedUpdates);
        }

        var backup = await CreateBackupAsync(request, load.PropsPath);
        if (backup.EarlyResult != null)
        {
            return backup.EarlyResult;
        }

        await ApplyUpdatesAsync(load.PropsPath, load.CurrentVersions, updatesToApply);

        return await RestoreTestAndFinalizeAsync(
            load.BasePath, backup.Path, backup.Manifest,
            load.CurrentVersions.Count, acceptedUpdates, updatesToApply, transitiveFound);
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

    private async Task<(List<PackageUpdateEntry> Updates, int TransitiveFound)> QueryAllUpdatesAsync(
        Dictionary<string, HashSet<string>> currentVersions,
        List<string> projectPaths,
        PackageUpdateRequest request)
    {
        var updates = await QueryNuGetForUpdatesAsync(currentVersions, request.IncludePrerelease);

        var transitiveFound = 0;
        if (request.IncludeTransitive)
        {
            var (transitive, found) = await ScanAndQueryTransitiveUpdatesAsync(
                projectPaths, currentVersions, request.IncludePrerelease);
            transitiveFound = found;
            updates.AddRange(transitive);
        }

        return (updates, transitiveFound);
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
        var backupEntry = _backupManager.CreateBackupForProject(request.Backup, propsPath, timestamp);
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

    private async Task ApplyUpdatesAsync(
        string propsPath,
        Dictionary<string, HashSet<string>> currentVersions,
        List<PackageUpdateEntry> updatesToApply)
    {
        var directCount = updatesToApply.Count(u => !u.IsTransitive);
        var transitiveCount = updatesToApply.Count(u => u.IsTransitive);
        var applyMsg = $"Applying {directCount} direct update(s)";
        if (transitiveCount > 0)
        {
            applyMsg += $" and pinning {transitiveCount} transitive package(s)";
        }
        _consoleService.Info(applyMsg + "...");

        var newVersions = BuildUpdatedVersionDictionary(currentVersions, updatesToApply);
        var (content, _, _, _) = _propsGenerator.MergeExisting(propsPath, newVersions);
        await FileHelper.WriteAtomicAsync(propsPath, content);
        _consoleService.Success("Versions updated in Directory.Packages.props.");
    }

    private async Task<PackageUpdateResult> RestoreTestAndFinalizeAsync(
        string basePath, string backupPath, BackupManifest manifest,
        int packagesChecked, List<PackageUpdateEntry> acceptedUpdates,
        List<PackageUpdateEntry> updatesToApply, int transitiveFound)
    {
        var solutionPath = FindSolutionFile(basePath);
        var targetPath = solutionPath ?? basePath;

        _consoleService.Info("Running dotnet restore...");
        var (restoreOutput, restoreSuccess) = await _dotNetCli.RunRestoreAsync(targetPath);
        if (!restoreSuccess)
        {
            _consoleService.Error("dotnet restore failed. Rolling back...");
            _consoleService.Dim(restoreOutput);
            return await RollbackAndReturn(backupPath, manifest, packagesChecked, acceptedUpdates, transitiveFound);
        }

        _consoleService.Info("Running dotnet test...");
        var (testOutput, testSuccess) = await _dotNetCli.RunTestAsync(targetPath);

        if (!testSuccess)
        {
            _consoleService.Error("Tests failed! Rolling back...");
            _consoleService.Dim(testOutput);
            return await RollbackAndReturn(backupPath, manifest, packagesChecked, acceptedUpdates, transitiveFound);
        }

        var directApplied = updatesToApply.Where(u => !u.IsTransitive).ToList();
        var transitiveApplied = updatesToApply.Where(u => u.IsTransitive).ToList();
        var successMsg = $"All tests passed! Updated {directApplied.Count} package(s)";
        if (transitiveApplied.Count > 0)
        {
            successMsg += $" and pinned {transitiveApplied.Count} transitive package(s)";
        }
        _consoleService.Success(successMsg + ".");
        BackupManager.CleanupBackups(backupPath, manifest);

        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.Success,
            PackagesChecked = packagesChecked,
            PackagesUpdated = directApplied.Count,
            PackagesSkipped = packagesChecked - directApplied.Count,
            TransitivePackagesFound = transitiveFound,
            TransitivePackagesUpdated = transitiveApplied.Count,
            TestsPassed = true,
            Updates = acceptedUpdates
        };
    }

    public void Dispose()
    {
        _nuGetLookup.Dispose();
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

    private async Task<List<PackageUpdateEntry>> QueryNuGetForUpdatesAsync(
        Dictionary<string, HashSet<string>> currentVersions,
        bool includePrerelease)
    {
        var updates = new List<PackageUpdateEntry>();
        using var semaphore = new SemaphoreSlim(8);
        var tasks = new List<Task<PackageUpdateEntry?>>();

        foreach (var (packageName, versions) in currentVersions)
        {
            var currentVersion = ResolveCurrentVersion(versions);
            if (currentVersion == null)
            {
                _logger.LogWarning("Could not parse any version for {PackageName}, skipping", packageName);
                continue;
            }
            tasks.Add(QuerySinglePackageAsync(packageName, currentVersion, includePrerelease, semaphore));
        }

        var results = await Task.WhenAll(tasks);
        updates.AddRange(results.Where(r => r != null).Cast<PackageUpdateEntry>());

        return updates;
    }

    private static string? ResolveCurrentVersion(HashSet<string> versions)
    {
        if (versions.Count == 1)
        {
            return versions.First();
        }

        return versions
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v != null)
            .OrderByDescending(v => v)
            .FirstOrDefault()
            ?.ToNormalizedString();
    }

    private async Task<PackageUpdateEntry?> QuerySinglePackageAsync(
        string packageName,
        string currentVersion,
        bool includePrerelease,
        SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var latestVersion = await _nuGetLookup.GetLatestVersionAsync(packageName, includePrerelease);
            if (latestVersion == null)
            {
                _logger.LogWarning("Could not fetch version for {PackageName}, skipping", packageName);
                return null;
            }

            var currentNuGet = NuGetVersion.TryParse(currentVersion, out var parsed) ? parsed : null;
            if (currentNuGet == null)
            {
                _logger.LogWarning("Could not parse current version {Version} for {PackageName}", currentVersion, packageName);
                return null;
            }

            var isMajor = latestVersion.Major != currentNuGet.Major;

            return new PackageUpdateEntry(
                packageName,
                currentVersion,
                latestVersion.ToNormalizedString(),
                isMajor,
                !isMajor);
        }
        finally
        {
            semaphore.Release();
        }
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

    private static Dictionary<string, HashSet<string>> BuildUpdatedVersionDictionary(
        Dictionary<string, HashSet<string>> currentVersions,
        List<PackageUpdateEntry> updatesToApply)
    {
        var result = new Dictionary<string, HashSet<string>>(currentVersions, StringComparer.OrdinalIgnoreCase);

        foreach (var update in updatesToApply)
        {
            result[update.PackageName] = [update.LatestVersion];
        }

        return result;
    }

    /// <summary>
    /// Scans all projects for transitive dependencies, deduplicates them, excludes those already
    /// managed as direct deps, and queries NuGet for their latest versions.
    /// </summary>
    /// <returns>A tuple of (update entries, total transitive deps found before filtering).</returns>
    private async Task<(List<PackageUpdateEntry> Updates, int TotalFound)> ScanAndQueryTransitiveUpdatesAsync(
        List<string> projectPaths,
        Dictionary<string, HashSet<string>> currentVersions,
        bool includePrerelease)
    {
        _consoleService.Info("Scanning transitive dependencies...");

        var allTransitive = new List<PackageReference>();
        var anySuccess = false;

        foreach (var projectPath in projectPaths)
        {
            try
            {
                var (refs, success) = await _projectAnalyzer.ScanTransitivePackagesAsync(projectPath);
                if (success)
                {
                    allTransitive.AddRange(refs);
                    anySuccess = true;
                }
                else
                {
                    _logger.LogWarning("Transitive scan failed for {Project}", Path.GetFileName(projectPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transitive scan failed for {Project}", Path.GetFileName(projectPath));
            }
        }

        if (!anySuccess)
        {
            _consoleService.Warning("Could not scan transitive dependencies. Continuing with direct updates only.");
            return ([], 0);
        }

        var deduplicated = allTransitive
            .GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var highest = g
                    .Select(r => (Ref: r, Parsed: NuGetVersion.TryParse(r.Version, out var v) ? v : null))
                    .Where(x => x.Parsed != null)
                    .OrderByDescending(x => x.Parsed)
                    .FirstOrDefault();
                return highest.Ref;
            })
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();

        var totalFound = deduplicated.Count;

        var transitiveOnly = deduplicated
            .Where(r => !currentVersions.ContainsKey(r.PackageName))
            .ToList();

        if (transitiveOnly.Count == 0)
        {
            _consoleService.Info("No transitive updates found (all already managed as direct dependencies).");
            return ([], totalFound);
        }

        _consoleService.Info($"Found {transitiveOnly.Count} transitive dependencies to check...");

        using var semaphore = new SemaphoreSlim(8);
        var tasks = transitiveOnly.Select(r =>
            QuerySingleTransitivePackageAsync(r.PackageName, r.Version, includePrerelease, semaphore));

        var results = await Task.WhenAll(tasks);
        var updates = results.Where(r => r != null).Cast<PackageUpdateEntry>().ToList();

        return (updates, totalFound);
    }

    private async Task<PackageUpdateEntry?> QuerySingleTransitivePackageAsync(
        string packageName,
        string currentVersion,
        bool includePrerelease,
        SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var latestVersion = await _nuGetLookup.GetLatestVersionAsync(packageName, includePrerelease);
            if (latestVersion == null)
            {
                _logger.LogWarning("Could not fetch version for transitive dep {PackageName}, skipping", packageName);
                return null;
            }

            var currentNuGet = NuGetVersion.TryParse(currentVersion, out var parsed) ? parsed : null;
            if (currentNuGet == null)
            {
                _logger.LogWarning("Could not parse transitive version {Version} for {PackageName}", currentVersion, packageName);
                return null;
            }

            var isMajor = latestVersion.Major != currentNuGet.Major;

            return new PackageUpdateEntry(
                packageName,
                currentVersion,
                latestVersion.ToNormalizedString(),
                isMajor,
                !isMajor,
                IsTransitive: true);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<PackageUpdateResult> RollbackAndReturn(
        string backupPath,
        BackupManifest manifest,
        int packagesChecked,
        List<PackageUpdateEntry> updates,
        int transitivePackagesFound = 0)
    {
        try
        {
            foreach (var entry in manifest.Backups)
            {
                BackupManager.RestoreFile(backupPath, entry);
            }
            _consoleService.Success("Rolled back to previous versions.");
            BackupManager.CleanupBackups(backupPath, manifest);
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Rollback failed: {ex.Message}");
            _consoleService.Warning($"Manual recovery: restore from backup at {backupPath}");
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.FileOperationError,
                PackagesChecked = packagesChecked,
                TransitivePackagesFound = transitivePackagesFound,
                TestsPassed = false,
                WasRolledBack = false,
                Updates = updates
            };
        }

        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.TestFailure,
            PackagesChecked = packagesChecked,
            TransitivePackagesFound = transitivePackagesFound,
            TestsPassed = false,
            WasRolledBack = true,
            Updates = updates
        };
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
