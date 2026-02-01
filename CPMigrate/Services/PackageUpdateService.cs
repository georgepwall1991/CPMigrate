using CPMigrate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace CPMigrate.Services;

/// <summary>
/// Orchestrates updating NuGet packages to latest versions with test verification and rollback.
/// </summary>
public class PackageUpdateService : IPackageUpdateService
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

    /// <inheritdoc />
    public async Task<PackageUpdateResult> UpdatePackagesAsync(Options options)
    {
        // Step 1: Discover solution
        var solutionDir = Path.GetFullPath(options.SolutionFileDir);
        var (basePath, _) = await _projectAnalyzer.DiscoverProjectsFromSolutionAsync(solutionDir);

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
        var updates = await QueryNuGetForUpdatesAsync(currentVersions, options.IncludePrerelease);

        // Step 5: Build update list
        var availableUpdates = updates
            .Where(u => u.LatestVersion != u.CurrentVersion)
            .ToList();

        if (availableUpdates.Count == 0)
        {
            _consoleService.Success("Everything up to date!");
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = currentVersions.Count,
                PackagesSkipped = currentVersions.Count
            };
        }

        // Show available updates table
        ShowUpdatesTable(availableUpdates);

        // Step 6: Interactive wizard for major bumps
        var acceptedUpdates = RunMajorVersionWizard(availableUpdates);

        var updatesToApply = acceptedUpdates.Where(u => u.Accepted).ToList();
        if (updatesToApply.Count == 0)
        {
            _consoleService.Info("No updates selected.");
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = currentVersions.Count,
                PackagesSkipped = currentVersions.Count,
                Updates = acceptedUpdates
            };
        }

        // Step 7: Dry-run check
        if (options.DryRun)
        {
            _consoleService.DryRun($"Would update {updatesToApply.Count} package(s).");
            ShowDryRunSummary(updatesToApply);
            return new PackageUpdateResult
            {
                ExitCode = ExitCodes.Success,
                PackagesChecked = currentVersions.Count,
                PackagesUpdated = updatesToApply.Count,
                PackagesSkipped = currentVersions.Count - updatesToApply.Count,
                Updates = acceptedUpdates
            };
        }

        // Step 8: Backup
        string backupPath;
        try
        {
            backupPath = BackupManager.CreateBackupDirectory(options);
        }
        catch (IOException ex)
        {
            _consoleService.Error($"Failed to create backup directory: {ex.Message}");
            return new PackageUpdateResult { ExitCode = ExitCodes.FileOperationError };
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupEntry = _backupManager.CreateBackupForProject(options, propsPath, backupPath, timestamp);

        var manifest = new BackupManifest
        {
            Timestamp = timestamp,
            PropsFilePath = propsPath,
            PropsFileExisted = true,
            Backups = backupEntry != null ? [backupEntry] : []
        };
        await BackupManager.WriteManifestAsync(backupPath, manifest);

        // Step 9: Apply updates
        _consoleService.Info($"Applying {updatesToApply.Count} update(s)...");
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
            return await RollbackAndReturn(backupPath, manifest, currentVersions.Count, acceptedUpdates);
        }

        _consoleService.Info("Running dotnet test...");
        var (testOutput, testSuccess) = await _dotNetCli.RunTestAsync(targetPath);

        // Step 11: Handle result
        if (!testSuccess)
        {
            _consoleService.Error("Tests failed! Rolling back...");
            _consoleService.Dim(testOutput);
            return await RollbackAndReturn(backupPath, manifest, currentVersions.Count, acceptedUpdates);
        }

        // Step 12: Success
        _consoleService.Success($"All tests passed! Updated {updatesToApply.Count} package(s).");
        BackupManager.CleanupBackups(backupPath, manifest);

        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.Success,
            PackagesChecked = currentVersions.Count,
            PackagesUpdated = updatesToApply.Count,
            PackagesSkipped = currentVersions.Count - updatesToApply.Count,
            TestsPassed = true,
            Updates = acceptedUpdates
        };
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
        var semaphore = new SemaphoreSlim(8);
        var tasks = new List<Task<PackageUpdateEntry?>>();

        foreach (var (packageName, versions) in currentVersions)
        {
            var currentVersion = versions.First();
            tasks.Add(QuerySinglePackageAsync(packageName, currentVersion, includePrerelease, semaphore));
        }

        var results = await Task.WhenAll(tasks);
        updates.AddRange(results.Where(r => r != null).Cast<PackageUpdateEntry>());

        return updates;
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
        _consoleService.WriteLine();
        _consoleService.Banner("AVAILABLE UPDATES");
        _consoleService.WriteLine();

        foreach (var update in updates.OrderBy(u => u.PackageName))
        {
            var label = update.IsMajorUpdate ? " (MAJOR)" : "";
            _consoleService.Info($"  {update.PackageName}: {update.CurrentVersion} → {update.LatestVersion}{label}");
        }

        _consoleService.WriteLine();
    }

    private List<PackageUpdateEntry> RunMajorVersionWizard(List<PackageUpdateEntry> updates)
    {
        var result = new List<PackageUpdateEntry>();

        foreach (var update in updates)
        {
            if (!update.IsMajorUpdate)
            {
                // Auto-accept minor/patch
                result.Add(update with { Accepted = true });
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

    private async Task<PackageUpdateResult> RollbackAndReturn(
        string backupPath,
        BackupManifest manifest,
        int packagesChecked,
        List<PackageUpdateEntry> updates)
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
                TestsPassed = false,
                WasRolledBack = false,
                Updates = updates
            };
        }

        return new PackageUpdateResult
        {
            ExitCode = ExitCodes.TestFailure,
            PackagesChecked = packagesChecked,
            TestsPassed = false,
            WasRolledBack = true,
            Updates = updates
        };
    }
}
