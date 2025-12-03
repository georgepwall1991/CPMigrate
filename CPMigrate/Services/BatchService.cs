using CPMigrate.Models;
using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Service for batch processing multiple solutions.
/// </summary>
public class BatchService
{
    private readonly IConsoleService _consoleService;
    private readonly Func<Options, Task<MigrationResult>> _migrationExecutor;

    /// <summary>
    /// Default directories to exclude when scanning for solutions.
    /// </summary>
    public static readonly HashSet<string> DefaultExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        ".git",
        "packages",
        ".vs",
        ".idea",
        "TestResults",
        "artifacts",
        ".nuget"
    };

    /// <summary>
    /// Creates a new BatchService instance.
    /// </summary>
    /// <param name="consoleService">Console service for output.</param>
    /// <param name="migrationExecutor">Function to execute migration for a single solution.</param>
    public BatchService(IConsoleService consoleService, Func<Options, Task<MigrationResult>> migrationExecutor)
    {
        _consoleService = consoleService;
        _migrationExecutor = migrationExecutor;
    }

    /// <summary>
    /// Discovers all .sln files in a directory tree.
    /// </summary>
    /// <param name="rootPath">Root directory to search.</param>
    /// <param name="excludedDirectories">Directories to exclude from search.</param>
    /// <returns>List of solution file paths.</returns>
    public List<string> DiscoverSolutions(string rootPath, HashSet<string>? excludedDirectories = null)
    {
        var excluded = excludedDirectories ?? DefaultExcludedDirectories;
        var solutions = new List<string>();

        if (!Directory.Exists(rootPath))
        {
            return solutions;
        }

        try
        {
            DiscoverSolutionsRecursive(rootPath, solutions, excluded);
        }
        catch (UnauthorizedAccessException)
        {
            _consoleService.Warning($"Access denied to some directories in {rootPath}");
        }

        return solutions.OrderBy(s => s).ToList();
    }

    private void DiscoverSolutionsRecursive(string directory, List<string> solutions, HashSet<string> excluded)
    {
        try
        {
            // Add solution files in current directory
            var slnFiles = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly);
            solutions.AddRange(slnFiles);

            // Recurse into subdirectories
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(subDir);
                if (!excluded.Contains(dirName))
                {
                    DiscoverSolutionsRecursive(subDir, solutions, excluded);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (DirectoryNotFoundException)
        {
            // Directory was deleted while scanning
        }
    }

    /// <summary>
    /// Runs batch migration on all solutions in a directory.
    /// </summary>
    /// <param name="options">Migration options.</param>
    /// <returns>Batch result with all solution outcomes.</returns>
    public async Task<BatchResult> RunBatchAsync(Options options)
    {
        var batchDir = options.BatchDir!;
        var result = new BatchResult
        {
            Operation = options.Analyze ? "batch-analyze" : "batch-migrate",
            DryRun = options.DryRun
        };

        // Discover solutions
        var solutions = DiscoverSolutions(batchDir);

        if (solutions.Count == 0)
        {
            _consoleService.Error($"No solution files found in: {batchDir}");
            result.Errors.Add("No solution files found");
            return result;
        }

        _consoleService.Banner($"BATCH MODE - Found {solutions.Count} solution(s)");
        _consoleService.WriteLine();

        // Display discovered solutions
        foreach (var (sln, index) in solutions.Select((s, i) => (s, i)))
        {
            var relativePath = Path.GetRelativePath(batchDir, sln);
            _consoleService.Dim($"  [{index + 1}/{solutions.Count}] {relativePath}");
        }
        _consoleService.WriteLine();

        var solutionResults = new List<SolutionResult>();

        if (options.BatchParallel)
        {
            solutionResults = await RunParallelAsync(options, solutions);
        }
        else
        {
            solutionResults = await RunSequentialAsync(options, solutions);
        }

        // Build final result
        result.Solutions.AddRange(solutionResults);

        // Display summary
        WriteBatchSummary(result, batchDir);

        return result;
    }

    private async Task<List<SolutionResult>> RunSequentialAsync(Options options, List<string> solutions)
    {
        var results = new List<SolutionResult>();
        var batchDir = options.BatchDir!;

        for (var i = 0; i < solutions.Count; i++)
        {
            var sln = solutions[i];
            var relativePath = Path.GetRelativePath(batchDir, sln);
            var solutionName = Path.GetFileNameWithoutExtension(sln);
            var solutionDir = Path.GetDirectoryName(sln) ?? ".";

            _consoleService.WriteMarkup($"\n[cyan]▓▓▓ [{i + 1}/{solutions.Count}] {Markup.Escape(relativePath)} ▓▓▓[/]\n");

            try
            {
                // Create options for this solution
                var solutionOptions = CloneOptionsForSolution(options, solutionDir);

                var migrationResult = await _migrationExecutor(solutionOptions);

                results.Add(new SolutionResult
                {
                    Path = sln,
                    Name = solutionName,
                    Success = migrationResult.ExitCode == ExitCodes.Success,
                    ExitCode = migrationResult.ExitCode,
                    Summary = new OperationSummary
                    {
                        ProjectsProcessed = migrationResult.ProjectsProcessed,
                        PackagesFound = migrationResult.PackagesCentralized,
                        ConflictsResolved = migrationResult.ConflictsResolved
                    },
                    PropsFile = migrationResult.PropsFilePath
                });
            }
            catch (Exception ex)
            {
                _consoleService.Error($"Failed to process {relativePath}: {ex.Message}");

                results.Add(new SolutionResult
                {
                    Path = sln,
                    Name = solutionName,
                    Success = false,
                    ExitCode = ExitCodes.UnexpectedError,
                    Error = ex.Message
                });

                if (!options.BatchContinue)
                {
                    _consoleService.Warning("Stopping batch (use --batch-continue to continue on failure)");
                    break;
                }
            }
        }

        return results;
    }

    private async Task<List<SolutionResult>> RunParallelAsync(Options options, List<string> solutions)
    {
        var results = new ConcurrentBag<SolutionResult>();
        var batchDir = options.BatchDir!;

        await Parallel.ForEachAsync(solutions, async (sln, _) =>
        {
            var relativePath = Path.GetRelativePath(batchDir, sln);
            var solutionName = Path.GetFileNameWithoutExtension(sln);
            var solutionDir = Path.GetDirectoryName(sln) ?? ".";

            try
            {
                var solutionOptions = CloneOptionsForSolution(options, solutionDir);
                var migrationResult = await _migrationExecutor(solutionOptions);

                results.Add(new SolutionResult
                {
                    Path = sln,
                    Name = solutionName,
                    Success = migrationResult.ExitCode == ExitCodes.Success,
                    ExitCode = migrationResult.ExitCode,
                    Summary = new OperationSummary
                    {
                        ProjectsProcessed = migrationResult.ProjectsProcessed,
                        PackagesFound = migrationResult.PackagesCentralized,
                        ConflictsResolved = migrationResult.ConflictsResolved
                    },
                    PropsFile = migrationResult.PropsFilePath
                });
            }
            catch (Exception ex)
            {
                results.Add(new SolutionResult
                {
                    Path = sln,
                    Name = solutionName,
                    Success = false,
                    ExitCode = ExitCodes.UnexpectedError,
                    Error = ex.Message
                });
            }
        });

        return results.OrderBy(r => r.Path).ToList();
    }

    private Options CloneOptionsForSolution(Options options, string solutionDir)
    {
        return new Options
        {
            SolutionFileDir = solutionDir,
            OutputDir = solutionDir,
            ProjectFileDir = string.Empty,
            KeepAttributes = options.KeepAttributes,
            NoBackup = options.NoBackup,
            BackupDir = Path.Combine(solutionDir, ".cpmigrate_backup"),
            AddBackupToGitignore = options.AddBackupToGitignore,
            GitignoreDir = solutionDir,
            DryRun = options.DryRun,
            ConflictStrategy = options.ConflictStrategy,
            Rollback = false,
            Analyze = options.Analyze,
            Interactive = false,
            Output = options.Output,
            OutputFile = options.OutputFile,
            Quiet = true, // Suppress individual solution output in batch mode
            Fix = options.Fix,
            FixDryRun = options.FixDryRun
        };
    }

    private void WriteBatchSummary(BatchResult result, string batchDir)
    {
        _consoleService.WriteLine();
        _consoleService.Separator();

        var totals = result.Totals;

        if (result.Success)
        {
            _consoleService.Success($"BATCH COMPLETE: {totals.Succeeded}/{totals.Solutions} solutions processed successfully");
        }
        else
        {
            _consoleService.Warning($"BATCH COMPLETE: {totals.Succeeded}/{totals.Solutions} succeeded, {totals.Failed} failed");
        }

        _consoleService.WriteLine();
        _consoleService.Dim($"  Total projects processed: {totals.ProjectsProcessed}");
        _consoleService.Dim($"  Total packages found: {totals.PackagesFound}");
        _consoleService.Dim($"  Total conflicts resolved: {totals.ConflictsResolved}");

        // List failed solutions
        var failures = result.Solutions.Where(s => !s.Success).ToList();
        if (failures.Count > 0)
        {
            _consoleService.WriteLine();
            _consoleService.Warning("Failed solutions:");
            foreach (var failure in failures)
            {
                var relativePath = Path.GetRelativePath(batchDir, failure.Path);
                _consoleService.Error($"  {relativePath}: {failure.Error ?? "Unknown error"}");
            }
        }
    }
}

/// <summary>
/// Concurrent bag for parallel processing.
/// </summary>
file class ConcurrentBag<T> : System.Collections.Concurrent.ConcurrentBag<T>;
