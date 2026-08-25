using System.Collections.Concurrent;
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
    private readonly BaselineService _baselineService = new();

    private static readonly HashSet<string> _defaultExcludedDirectories = new(
        StringComparer.OrdinalIgnoreCase
    )
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
        ".nuget",
    };

    /// <summary>
    /// Default directories to exclude when scanning for solutions.
    /// </summary>
    public static IReadOnlySet<string> DefaultExcludedDirectories => _defaultExcludedDirectories;

    /// <summary>
    /// Creates a new BatchService instance.
    /// </summary>
    /// <param name="consoleService">Console service for output.</param>
    /// <param name="migrationExecutor">Function to execute migration for a single solution.</param>
    public BatchService(
        IConsoleService consoleService,
        Func<Options, Task<MigrationResult>> migrationExecutor
    )
    {
        _consoleService = consoleService;
        _migrationExecutor = migrationExecutor;
    }

    /// <summary>
    /// Discovers all solution files (.sln and .slnx) in a directory tree.
    /// </summary>
    /// <param name="rootPath">Root directory to search.</param>
    /// <param name="excludedDirectories">Directories to exclude from search.</param>
    /// <returns>List of solution file paths.</returns>
    public List<string> DiscoverSolutions(
        string rootPath,
        HashSet<string>? excludedDirectories = null
    )
    {
        var excluded =
            excludedDirectories
            ?? new HashSet<string>(DefaultExcludedDirectories, StringComparer.OrdinalIgnoreCase);
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

    private void DiscoverSolutionsRecursive(
        string directory,
        List<string> solutions,
        HashSet<string> excluded,
        HashSet<string>? visitedPaths = null
    )
    {
        // Track visited directories to avoid infinite loops from symlinks
        visitedPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Get the real path to detect symlink loops
            var realPath = Path.GetFullPath(directory);
            if (!visitedPaths.Add(realPath))
            {
                // Already visited this directory (circular symlink)
                return;
            }

            // Check if this is a symlink/reparse point and skip if so
            var dirInfo = new DirectoryInfo(directory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _consoleService.Dim($"  Skipping symlink: {directory}");
                return;
            }

            // Add solution files in current directory (both .sln and .slnx)
            var slnFiles = ProjectAnalyzer.GetSolutionFiles(directory);
            solutions.AddRange(slnFiles);

            // Recurse into subdirectories
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(subDir);
                if (!excluded.Contains(dirName))
                {
                    DiscoverSolutionsRecursive(subDir, solutions, excluded, visitedPaths);
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
            DryRun = options.DryRun,
        };

        // Discover solutions
        var solutions = DiscoverSolutions(batchDir);

        if (solutions.Count == 0)
        {
            _consoleService.Error($"No solution files found in: {batchDir}");
            result.Errors.Add("No solution files found");
            result.Timestamp = DateTime.UtcNow.ToString("o");
            WriteReportIfRequested(options, result);
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

        List<SolutionResult> solutionResults;

        if (options.BatchParallel)
        {
            solutionResults = await RunParallelAsync(options, solutions);
        }
        else
        {
            solutionResults = await RunSequentialAsync(options, solutions);
        }

        // Build final result. "Complete" means every discovered solution ran AND applied the
        // baseline: a failed solution contributes no matched fingerprints, so counting it would
        // declare its live debt stale.
        result.Solutions.AddRange(solutionResults);
        result.BaselineVerdictComplete =
            result.Solutions.Count == solutions.Count
            && result.Solutions.All(s => s.MatchedBaselineFingerprints is not null);

        // A shared baseline spans solutions: an entry accepted for solution A is unmatched noise in
        // solution B's own count, so per-solution sums double-report live debt as fixed. The batch
        // verdict is computed once, against the union of what every solution actually matched — and
        // only when the batch is complete, because a partial run cannot tell live debt from fixed.
        // Known limit: fingerprints identify projects relative to their scan root, so this verdict
        // assumes the baseline was recorded against the same solution roots the batch walked.

        if (options.UsesBaseline() && result.BaselineVerdictComplete)
        {
            var baselinePath = options.ResolveBaselinePath();
            var (baseline, readError) = _baselineService.Read(baselinePath);
            if (baseline is not null)
            {
                var matched = result.Solutions
                    .SelectMany(s => s.MatchedBaselineFingerprints ?? [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // A rule no solution could judge keeps its entry out of "fixed" — the finding may
                // still be live where nobody looked.
                var unevaluated = result.Solutions
                    .SelectMany(s => s.UnevaluatedRuleCodes ?? [])
                    .ToHashSet();

                var staleFindings = baseline
                    .Findings
                    .Where(f =>
                        !matched.Contains(f.Fingerprint)
                        && BaselineService.IsKnownRuleId(f.IssueCode)
                        && !(
                            Enum.TryParse<AnalysisIssueCode>(f.IssueCode, true, out var code)
                            && unevaluated.Contains(code)
                        )
                    )
                    .ToList();

                // Unknown IDs are classified against the whole baseline: an entry whose fingerprint
                // still matches somewhere still cites a rule that no longer exists.
                var unknownCodes = baseline
                    .Findings
                    .Select(f => f.IssueCode)
                    .Where(code => !BaselineService.IsKnownRuleId(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .ToList();

                result.BaselineStaleEntries = staleFindings.Count;
                result.BaselineUnknownRuleCodes = unknownCodes;
            }
            else if (!_consoleService.IsInteractive || readError is not null)
            {
                // Read failures are already surfaced per-solution; stay silent here rather than
                // inventing a second channel. No rot verdict without the file.
            }
        }

        // The report's date is the completion date: the default was captured at construction,
        // before discovery and processing, which is wrong for a long batch.
        result.Timestamp = DateTime.UtcNow.ToString("o");

        // Display summary
        WriteBatchSummary(result, batchDir);

        WriteReportIfRequested(options, result);

        return result;
    }

    private void WriteReportIfRequested(Options options, BatchResult result)
    {
        // Persistent rollup for CI/PR attachment — a file a team can keep, unlike the console
        // summary. Written on every completed batch, including an empty one: an explicitly
        // requested artifact must exist even when there is nothing to summarize.
        if (!string.IsNullOrEmpty(options.ReportPath))
        {
            BatchReportWriter.Write(result, options.ReportPath);
            if (!options.Quiet)
            {
                _consoleService.Dim($"Batch report written to: {options.ReportPath}");
            }
        }
    }

    private async Task<List<SolutionResult>> RunSequentialAsync(
        Options options,
        List<string> solutions
    )
    {
        var results = new List<SolutionResult>();
        var batchDir = options.BatchDir!;

        for (var i = 0; i < solutions.Count; i++)
        {
            var sln = solutions[i];
            var relativePath = Path.GetRelativePath(batchDir, sln);
            var solutionName = Path.GetFileNameWithoutExtension(sln);
            var solutionDir = Path.GetDirectoryName(sln) ?? ".";

            _consoleService.WriteMarkup(
                $"\n[cyan]▓▓▓ [[{i + 1}/{solutions.Count}]] {Markup.Escape(relativePath)} ▓▓▓[/]\n"
            );

            try
            {
                // Create options for this solution (include solution name for unique backup dirs)
                var solutionOptions = CloneOptionsForSolution(options, solutionDir, solutionName);

                var migrationResult = await _migrationExecutor(solutionOptions);

                results.Add(
                    new SolutionResult
                    {
                        Path = sln,
                        Name = solutionName,
                        Success = migrationResult.ExitCode == ExitCodes.Success,
                        ExitCode = migrationResult.ExitCode,
                        Summary = BuildSolutionSummary(migrationResult, solutionOptions),
                        PropsFile = migrationResult.PropsFilePath,
                        MatchedBaselineFingerprints =
                            migrationResult.BaselineMatchedFingerprints,
                        UnevaluatedRuleCodes =
                            migrationResult.BaselineUnevaluatedRuleCodes,
                    }
                );
            }
            catch (Exception ex)
            {
                _consoleService.Error($"Failed to process {relativePath}: {ex.Message}");

                results.Add(
                    new SolutionResult
                    {
                        Path = sln,
                        Name = solutionName,
                        Success = false,
                        ExitCode = ExitCodes.UnexpectedError,
                        Error = ex.Message,
                    }
                );

                if (!options.BatchContinue)
                {
                    _consoleService.Warning(
                        "Stopping batch (use --batch-continue to continue on failure)"
                    );
                    break;
                }
            }
        }

        return results;
    }

    private async Task<List<SolutionResult>> RunParallelAsync(
        Options options,
        List<string> solutions
    )
    {
        var results = new ConcurrentBag<SolutionResult>();
        using var cts = new CancellationTokenSource();
        var hasFailure = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cts.Token,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        try
        {
            await Parallel.ForEachAsync(
                solutions,
                parallelOptions,
                async (sln, ct) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    var solutionName = Path.GetFileNameWithoutExtension(sln);
                    var solutionDir = Path.GetDirectoryName(sln) ?? ".";

                    try
                    {
                        var solutionOptions = CloneOptionsForSolution(
                            options,
                            solutionDir,
                            solutionName
                        );
                        var migrationResult = await _migrationExecutor(solutionOptions);

                        results.Add(
                            CreateSolutionResult(
                                sln,
                                solutionName,
                                migrationResult,
                                solutionOptions
                            )
                        );

                        if (migrationResult.ExitCode != ExitCodes.Success && !options.BatchContinue)
                        {
                            SignalFailureAndCancel(cts, ref hasFailure);
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(CreateFailedResult(sln, solutionName, ex));

                        if (!options.BatchContinue)
                        {
                            SignalFailureAndCancel(cts, ref hasFailure);
                        }
                    }
                }
            );
        }
        catch (OperationCanceledException)
        {
            if (Interlocked.CompareExchange(ref hasFailure, 0, 0) == 1 && !options.BatchContinue)
            {
                _consoleService.Warning(
                    "Stopping batch (use --batch-continue to continue on failure)"
                );
            }
        }

        return results.OrderBy(r => r.Path).ToList();
    }

    private static SolutionResult CreateSolutionResult(
        string sln,
        string solutionName,
        MigrationResult migrationResult,
        Options solutionOptions
    )
    {
        return new SolutionResult
        {
            Path = sln,
            Name = solutionName,
            Success = migrationResult.ExitCode == ExitCodes.Success,
            ExitCode = migrationResult.ExitCode,
            Summary = BuildSolutionSummary(migrationResult, solutionOptions),
            PropsFile = migrationResult.PropsFilePath,
            MatchedBaselineFingerprints = migrationResult.BaselineMatchedFingerprints,
            UnevaluatedRuleCodes = migrationResult.BaselineUnevaluatedRuleCodes,
        };
    }

    /// <summary>
    /// Builds a solution's summary, including the analysis gate metadata. Batch output advertises
    /// the same JSON schema as a single-solution run, so omitting these fields would leave a
    /// consumer unable to tell a below-threshold batch result from a genuinely clean one.
    /// </summary>
    private static OperationSummary BuildSolutionSummary(
        MigrationResult migrationResult,
        Options solutionOptions
    )
    {
        var report = migrationResult.AnalysisReport;

        // Per-solution runs are quiet, so the terminal notice never reaches a batch consumer. Without
        // the policy in the payload, findings a batch run configured away would be indistinguishable
        // from a solution that had none.
        var rulePolicy = report is null ? RulePolicy.Empty : solutionOptions.ResolveRulePolicy();

        return new OperationSummary
        {
            DisabledRules = rulePolicy.ReportedDisabledRules(),
            SeverityOverrides = rulePolicy.ReportedSeverityOverrides(),
            ProjectsProcessed = migrationResult.ProjectsProcessed,
            PackagesFound = migrationResult.PackagesCentralized,
            ConflictsResolved = migrationResult.ConflictsResolved,
            IssuesFound = report?.TotalIssues ?? 0,
            FailOnSeverity = report is null ? null : solutionOptions.FailOn.ToString(),
            IssuesAtOrAboveThreshold = migrationResult.GatedIssueCount,
            IssuesRemainingAfterFixes = migrationResult.PostFixAnalysisReport?.TotalIssues,
            HighestSeverity = report?.HighestSeverity?.ToString(),
            ScanFailures = report is null ? null : migrationResult.ScanFailures,
            DeepScanFailures = report is null ? null : migrationResult.DeepScanFailures,
            // Staleness is deliberately absent here: each solution applies the whole shared
            // baseline, so its local unmatched entries include other solutions' live debt. A
            // per-solution count would label cross-solution findings as fixed. The authoritative
            // batch-wide verdict goes to the terminal summary and the --report artifact instead.
        };
    }

    private static SolutionResult CreateFailedResult(string sln, string solutionName, Exception ex)
    {
        return new SolutionResult
        {
            Path = sln,
            Name = solutionName,
            Success = false,
            ExitCode = ExitCodes.UnexpectedError,
            Error = ex.Message,
        };
    }

    private static void SignalFailureAndCancel(CancellationTokenSource cts, ref int hasFailure)
    {
        Interlocked.Exchange(ref hasFailure, 1);
        cts.Cancel();
    }

    private static Options CloneOptionsForSolution(
        Options options,
        string solutionDir,
        string? solutionName = null
    )
    {
        // Solution-specific backup directory, so parallel runs cannot collide.
        var backupDirName = string.IsNullOrEmpty(solutionName)
            ? ".cpmigrate_backup"
            : $".cpmigrate_backup_{solutionName}";

        return options.CloneForBatchSolution(solutionDir, backupDirName);
    }

    private void WriteBatchSummary(BatchResult result, string batchDir)
    {
        _consoleService.WriteLine();
        _consoleService.Separator();

        var totals = result.Totals;

        if (result.Success)
        {
            _consoleService.Success(
                $"BATCH COMPLETE: {totals.Succeeded}/{totals.Solutions} solutions processed successfully"
            );
        }
        else
        {
            _consoleService.Warning(
                $"BATCH COMPLETE: {totals.Succeeded}/{totals.Solutions} succeeded, {totals.Failed} failed"
            );
        }

        _consoleService.WriteLine();
        _consoleService.Dim($"  Total projects processed: {totals.ProjectsProcessed}");
        _consoleService.Dim($"  Total packages found: {totals.PackagesFound}");
        _consoleService.Dim($"  Total conflicts resolved: {totals.ConflictsResolved}");

        // Per-solution runs are quiet, so ApplyBaseline's warnings never printed; this summary is
        // where batch baseline rot gets its airtime. The batch-level verdict — computed against
        // what every solution matched — is authoritative when it exists; per-solution counts are a
        // single-solution view and summing them would double-report cross-solution debt.
        var staleEntries = result.BaselineVerdictComplete
            ? result.BaselineStaleEntries
                ?? result.Solutions.Sum(s => s.Summary?.BaselineStaleEntries ?? 0)
            : 0;
        if (staleEntries > 0)
        {
            _consoleService.Warning(
                $"{staleEntries} baseline entr(ies) across the batch matched no finding (fixed "
                    + "since). Remove the dead entries from each solution's baseline file by hand; --write-baseline would also accept new findings."
            );
        }

        var unknownCodes = (
            result.BaselineVerdictComplete
                ? result.BaselineUnknownRuleCodes
                    ?? result.Solutions
                        .SelectMany(s => s.Summary?.BaselineUnknownRuleCodes ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                : []
        );
        if (unknownCodes.Count > 0)
        {
            _consoleService.Warning(
                "The baselines cite rule ID(s) CPMigrate does not know ("
                    + $"{string.Join(", ", unknownCodes)}) — likely renamed or removed rules. Run "
                    + "'cpmigrate --explain all' for the current rule IDs."
            );
        }

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
