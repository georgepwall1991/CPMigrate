using CPMigrate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates updating NuGet packages to latest versions with test verification and rollback.
/// </summary>
public class PackageUpdateService : IPackageUpdateService, IDisposable
{
    private readonly IConsoleService _consoleService;
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly PropsGenerator _propsGenerator;
    private readonly INuGetVersionLookupService _nuGetLookup;
    private readonly IDotNetCliService _dotNetCli;
    private readonly IBackupManager _backupManager;
    private readonly ILogger<PackageUpdateService> _logger;
    private bool _disposed;

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
        // Step 1: Discover solution
        var solutionDir = Path.GetFullPath(request.SolutionPath);
        var (basePath, projectPaths) = await _projectAnalyzer.DiscoverProjectsFromSolutionAsync(solutionDir);

        // Step 2: Find Directory.Packages.props
        var propsPath = FindPropsFile(basePath);
        if (propsPath == null)
        {
            _consoleService.Error("Directory.Packages.props not found. CPM is not enabled. Run 'cpmigrate' first.");
            return new PackageUpdateResult { ExitCode = ExitCodes.ValidationError };
        }

        // Step 3: Read current package versions
        var currentVersions = PropsGenerator.ReadExistingPackageVersions(propsPath, out _);
        if (currentVersions.Count == 0)
        {
            _consoleService.Info("No packages found in Directory.Packages.props.");
            return new PackageUpdateResult { ExitCode = ExitCodes.Success };
        }

        // Step 4: Query NuGet for latest versions
        _consoleService.Info($"Checking {currentVersions.Count} packages for updates...");
        var updates = await QueryNuGetForUpdatesAsync(currentVersions, request.IncludePrerelease);

        // Step 4b: If --transitive, scan transitive deps
        var transitiveFound = 0;
        if (request.IncludeTransitive)
        {
            var (transitive, found) = await ScanAndQueryTransitiveUpdatesAsync(
                projectPaths, currentVersions, request.IncludePrerelease);
            transitiveFound = found;
            updates.AddRange(transitive);
        }

        // Step 5: Build update list — use semantic version comparison, not string equality
        var availableUpdates = updates
            .Where(u =>
            {
                var current = NuGetVersion.TryParse(u.CurrentVersion, out var c) ? c : null;
                var latest = NuGetVersion.TryParse(u.LatestVersion, out var l) ? l : null;
                return current != null && latest != null && latest > current;
            })
            .ToList();

        if (availableUpdates.Count == 0)
        {
            _consoleService.Success("Everything up to date!");
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = currentVersions.Count,
                PackagesSkipped = currentVersions.Count,
                TransitivePackagesFound = transitiveFound
            };
        }

        // Show available updates table
        ShowUpdatesTable(availableUpdates);

        // Step 6: Interactive wizard for major bumps
        var acceptedUpdates = RunMajorVersionWizard(availableUpdates, request);

        var updatesToApply = acceptedUpdates.Where(u => u.Accepted).ToList();
        if (updatesToApply.Count == 0)
        {
            _consoleService.Info("No updates selected.");
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = currentVersions.Count,
                PackagesSkipped = currentVersions.Count,
                TransitivePackagesFound = transitiveFound,
                Updates = acceptedUpdates
            };
        }

        // Step 7: Dry-run check
        if (request.DryRun)
        {
            var directDryRun = updatesToApply.Where(u => !u.IsTransitive).ToList();
            var transitiveDryRun = updatesToApply.Where(u => u.IsTransitive).ToList();
            _consoleService.DryRun($"Would update {directDryRun.Count} direct package(s)" +
                (transitiveDryRun.Count > 0 ? $" and pin {transitiveDryRun.Count} transitive package(s)." : "."));
            ShowDryRunSummary(updatesToApply);
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = currentVersions.Count,
                PackagesUpdated = directDryRun.Count,
                PackagesSkipped = currentVersions.Count - directDryRun.Count,
                TransitivePackagesFound = transitiveFound,
                TransitivePackagesUpdated = transitiveDryRun.Count,
                Updates = acceptedUpdates
            };
        }

        // Step 8: Backup
        string backupPath;
        try
        {
            backupPath = BackupManager.CreateBackupDirectory(request.Backup);
        }
        catch (IOException ex)
        {
            _consoleService.Error($"Failed to create backup directory: {ex.Message}");
            return new PackageUpdateResult { ExitCode = ExitCodes.FileOperationError };
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

        // Step 9: Apply updates
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

        // Step 10: Restore + Test
        var solutionPath = FindSolutionFile(basePath);
        var targetPath = solutionPath ?? basePath;

        _consoleService.Info("Running dotnet restore...");
        var (restoreOutput, restoreSuccess) = await _dotNetCli.RunRestoreAsync(targetPath);
        if (!restoreSuccess)
        {
            _consoleService.Error("dotnet restore failed. Rolling back...");
            _consoleService.Dim(restoreOutput);
            return await RollbackAndReturn(backupPath, manifest, currentVersions.Count, acceptedUpdates, transitiveFound);
        }

        _consoleService.Info("Running dotnet test...");
        var (testOutput, testSuccess) = await _dotNetCli.RunTestAsync(targetPath);

        // Step 11: Handle result
        if (!testSuccess)
        {
            _consoleService.Error("Tests failed! Rolling back...");
            _consoleService.Dim(testOutput);
            return await RollbackAndReturn(backupPath, manifest, currentVersions.Count, acceptedUpdates, transitiveFound);
        }

        // Step 12: Success
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
            PackagesChecked = currentVersions.Count,
            PackagesUpdated = directApplied.Count,
            PackagesSkipped = currentVersions.Count - directApplied.Count,
            TransitivePackagesFound = transitiveFound,
            TransitivePackagesUpdated = transitiveApplied.Count,
            TestsPassed = true,
            Updates = acceptedUpdates
        };
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing && _nuGetLookup is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
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
            // Resolve to highest version when multiple exist (version conflicts)
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

        // When multiple versions exist (conflicts), pick the highest
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
                !isMajor); // Auto-accept minor/patch, defer major to wizard
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
        var nonInteractive = request.Output.IsNonInteractive;

        foreach (var update in updates)
        {
            if (!update.IsMajorUpdate)
            {
                // Auto-accept minor/patch
                result.Add(update with { Accepted = true });
                continue;
            }

            if (nonInteractive)
            {
                // Keep CI / JSON mode deterministic: skip major bumps unless explicitly run interactively.
                result.Add(update with { Accepted = false });
                continue;
            }

            // Prompt for major version bump
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
        // Start with current versions
        var result = new Dictionary<string, HashSet<string>>(currentVersions, StringComparer.OrdinalIgnoreCase);

        // Override with accepted updates
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

        // Scan all projects
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

        // Deduplicate: group by package name (case-insensitive), pick highest resolved version
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

        // Exclude packages already managed as direct dependencies
        var transitiveOnly = deduplicated
            .Where(r => !currentVersions.ContainsKey(r.PackageName))
            .ToList();

        if (transitiveOnly.Count == 0)
        {
            _consoleService.Info("No transitive updates found (all already managed as direct dependencies).");
            return ([], totalFound);
        }

        _consoleService.Info($"Found {transitiveOnly.Count} transitive dependencies to check...");

        // Query NuGet for latest versions
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
                !isMajor, // Auto-accept minor/patch, defer major to wizard
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
