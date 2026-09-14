using CPMigrate.Models;
using CPMigrate.Services.Migration;
using CPMigrate.Services.Verify;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace CPMigrate.Services;

public class BuildPropsService
{
    /// <summary>
    /// Minimum percentage of projects that must share the same property value
    /// for it to be considered a unification candidate.
    /// </summary>
    private const double ConsensusThresholdPercent = 0.6;

    private readonly IConsoleService _consoleService;
    private readonly BuildPropsAnalyzer _analyzer;
    private readonly IProjectAnalyzer _projectAnalyzer;
    private readonly IBackupManager _backupManager;
    private readonly MigrationVerifier? _verifier;
    private readonly RollbackHandler? _rollbackHandler;
    private readonly MigrationDisplay _display;

    public BuildPropsService(
        IConsoleService consoleService,
        IProjectAnalyzer projectAnalyzer,
        IBackupManager? backupManager = null)
        : this(consoleService, projectAnalyzer, backupManager, verifier: null, rollbackHandler: null)
    {
    }

    internal BuildPropsService(
        IConsoleService consoleService,
        IProjectAnalyzer projectAnalyzer,
        IBackupManager? backupManager,
        MigrationVerifier? verifier,
        RollbackHandler? rollbackHandler)
    {
        _consoleService = consoleService;
        _projectAnalyzer = projectAnalyzer;
        _analyzer = new BuildPropsAnalyzer(consoleService);
        _backupManager = backupManager ?? new BackupManager();
        _verifier = verifier;
        _rollbackHandler = rollbackHandler;
        _display = new MigrationDisplay(consoleService);
    }

    public async Task<int> UnifyPropertiesAsync(Options options)
    {
        var startDir = !string.IsNullOrEmpty(options.SolutionFileDir) ? options.SolutionFileDir : ".";
        var (basePath, projectPaths) = await _projectAnalyzer.DiscoverProjectsFromSolutionAsync(startDir);

        if (projectPaths.Count == 0)
        {
            _consoleService.Error("No projects found to analyze.");
            if (options.Output == OutputFormat.Json)
            {
                await EmitJsonAsync(options, basePath, "failed", ExitCodes.UnexpectedError, [], [], new PropertyAnalysisResult { TotalProjects = 0 }, 0);
            }
            return ExitCodes.UnexpectedError;
        }

        _consoleService.Banner("Analyzing Project Properties...");
        var analysis = _analyzer.Analyze(projectPaths);

        var threshold = GetConsensusThreshold(analysis.TotalProjects);
        var propertyCandidates = FindPropertyCandidates(analysis, threshold);
        var itemCandidates = FindItemCandidates(analysis, threshold);

        if (propertyCandidates.Count == 0 && itemCandidates.Count == 0)
        {
            _consoleService.Info($"No common properties or items found (checked for >{ConsensusThresholdPercent:P0} consensus).");
            if (options.Output == OutputFormat.Json)
            {
                await EmitJsonAsync(options, basePath, "noCandidates", ExitCodes.Success, [], [], analysis, 0);
            }
            return ExitCodes.Success;
        }

        DisplayCandidates(propertyCandidates, itemCandidates, analysis);

        if (options.DryRun)
        {
            _consoleService.DryRun("Would create/update Directory.Build.props with these items.");
            _consoleService.DryRun("Would remove these items from matching project files.");
            if (options.Output == OutputFormat.Json)
            {
                await EmitJsonAsync(options, basePath, "dryRun", ExitCodes.Success, propertyCandidates, itemCandidates, analysis, 0);
            }
            return ExitCodes.Success;
        }

        if (!options.Force)
        {
            // Rewriting every matching project file is a write the operator has not confirmed.
            // On a non-prompting terminal, report what would happen and stop.
            if (!_consoleService.IsInteractive)
            {
                _consoleService.Warning("Cannot prompt for confirmation on a non-interactive terminal.");
                _consoleService.Info("Re-run with --force to unify these, or --dry-run to preview them.");
                if (options.Output == OutputFormat.Json)
                {
                    await EmitJsonAsync(options, basePath, "refused", ExitCodes.Success, propertyCandidates, itemCandidates, analysis, 0);
                }
                return ExitCodes.Success;
            }

            if (!_consoleService.AskConfirmation("Do you want to move these to Directory.Build.props?"))
            {
                if (options.Output == OutputFormat.Json)
                {
                    await EmitJsonAsync(options, basePath, "refused", ExitCodes.Success, propertyCandidates, itemCandidates, analysis, 0);
                }
                return ExitCodes.Success;
            }
        }

        return await RunUnificationAsync(
            options, basePath, projectPaths, propertyCandidates, itemCandidates, analysis);
    }

    /// <summary>
    /// The confirmed pass: baseline capture, the writes, then verification and its rollback. Split
    /// from <see cref="UnifyPropertiesAsync"/> at the consent boundary — everything before it costs
    /// nothing, everything after it is the mutation the gates above were guarding.
    /// </summary>
    private async Task<int> RunUnificationAsync(
        Options options,
        string basePath,
        List<string> projectPaths,
        List<PropertyCandidate> propertyCandidates,
        List<ItemCandidate> itemCandidates,
        PropertyAnalysisResult analysis)
    {
        var propsList = propertyCandidates.Select(c => c.Property).ToList();
        var itemsList = itemCandidates.Select(c => c.Item).ToList();
        var buildPropsPath = Path.Combine(basePath, "Directory.Build.props");

        // --verify keeps the migration's contract: the baseline is captured after the run is
        // confirmed but before a byte is written, because "did not restore beforehand" is a reason
        // to stop rather than to proceed unmeasured.
        GraphSnapshotResult? baseline = null;
        if (options.Verify && _verifier is not null)
        {
            baseline = await _verifier.CaptureAsync(
                MigrationService.RestoreTarget(options),
                projectPaths,
                basePath
            );

            if (!baseline.RestoreSucceeded)
            {
                _consoleService.Error(
                    "The solution does not restore before unification — nothing was written. "
                        + Tail(baseline.RestoreOutput)
                );
                var baselineFailure = new VerificationReport(
                    VerificationVerdict.Failed,
                    ProjectsRestored: 0,
                    ProjectsExpected: 0,
                    ResolvedVersionCount: 0,
                    UnchangedCount: 0,
                    [],
                    [],
                    [],
                    "the solution did not restore before unification, so there is no baseline "
                        + $"to measure it against. {Tail(baseline.RestoreOutput)}"
                );
                if (options.Output == OutputFormat.Json)
                {
                    await EmitJsonAsync(options, basePath, "failed", ExitCodes.GraphDrift, propertyCandidates, itemCandidates, analysis, 0, verification: baselineFailure);
                }
                return ExitCodes.GraphDrift;
            }
        }

        // The pass rewrites every consensus project file exactly like a migration does, so it owes
        // the same undo path: each file lands in .cpmigrate_backup before its first write, under the
        // manifest --rollback already reads.
        var backupSession = FixBackupSession.TryCreate(
            BackupSettings.FromOptions(options),
            buildPropsPath,
            options.DryRun,
            _backupManager);

        await CreateOrUpdateBuildProps(buildPropsPath, propsList, itemsList, backupSession);
        var filesModified = 1; // the props file itself is always written
        filesModified += await RemovePropertiesFromProjects(projectPaths, propsList, backupSession);
        filesModified += await RemoveItemsFromProjects(projectPaths, itemsList, backupSession);

        backupSession?.WriteManifest();
        if (backupSession is { FileCount: > 0 })
        {
            _consoleService.Dim(
                $"Backed up {backupSession.FileCount} file(s) to {backupSession.BackupPath} "
                    + "- undo with --rollback."
            );
        }

        VerificationReport? verification = null;
        var exitCode = ExitCodes.Success;
        if (baseline is not null && _verifier is not null)
        {
            verification = await VerifyUnifiedTreeAsync(
                options,
                projectPaths,
                basePath,
                baseline,
                itemCandidates,
                backupSession,
                _verifier
            );
            if (!verification.Passed(options.VerifyStrict))
            {
                exitCode = ExitCodes.GraphDrift;
            }
        }

        if (exitCode == ExitCodes.Success)
        {
            _consoleService.Success($"Successfully unified {propertyCandidates.Count} properties and {itemCandidates.Count} items.");
        }
        if (options.Output == OutputFormat.Json)
        {
            // A failed verification reports 'failed' even though the writes happened — when the
            // pass rolled back, the tree on disk no longer holds them, and 'unified' would tell a
            // consumer gating on the token that they do.
            var status = exitCode == ExitCodes.Success ? "unified" : "failed";
            await EmitJsonAsync(options, basePath, status, exitCode, propertyCandidates, itemCandidates, analysis, filesModified, backupSession, verification);
        }
        return exitCode;
    }

    /// <summary>
    /// Emits the machine-readable document. The candidates are the same list the console printed —
    /// a consumer reading the document alone gets the same verdict a shell script would.
    /// </summary>
    private async Task EmitJsonAsync(
        Options options,
        string basePath,
        string status,
        int exitCode,
        List<PropertyCandidate> propertyCandidates,
        List<ItemCandidate> itemCandidates,
        PropertyAnalysisResult analysis,
        int filesModified,
        FixBackupSession? backupSession = null,
        VerificationReport? verification = null
    )
    {
        // Projects are looked up by the candidate's exact occurrence key — name+value for
        // properties, type+include+metadata for items. Matching on identity alone would list a
        // project that holds the item under different metadata as a declarer of a thing it does
        // not declare, and a consumer reconciling count against projects.Length would find them
        // disagreeing.
        var candidates = new UnifyPropsCandidatesPayload(
            propertyCandidates.Select(c => new UnifyPropsPropertyPayload(
                c.Property.Name,
                c.Property.Value,
                c.Count,
                analysis.PropertyOccurrences
                    .TryGetValue($"{c.Property.Name}|{c.Property.Value}", out var holders)
                    ? holders.Select(p => p.ProjectPath).ToList()
                    : [],
                analysis.TotalProjects - c.Count
            )).ToList(),
            itemCandidates.Select(c => new UnifyPropsItemPayload(
                c.Item.ItemType,
                c.Item.Include,
                c.Count,
                analysis.ItemOccurrences
                    .TryGetValue(CreateLookupKey(c.Item), out var holders)
                    ? holders.Select(i => i.ProjectPath).ToList()
                    : [],
                c.Item.Metadata,
                analysis.TotalProjects - c.Count
            )).ToList()
        );
        var summary = new UnifyPropsSummaryPayload(
            analysis.TotalProjects,
            propertyCandidates.Count,
            itemCandidates.Count,
            filesModified
        );
        BackupInfo? backup = backupSession is { ManifestWritten: true }
            ? new BackupInfo { Path = backupSession.BackupPath!, FilesBackedUp = backupSession.FileCount }
            : null;
        await JsonOutputWriter.EmitAsync(
            UnifyPropsJsonWriter.Serialize(
                Path.Combine(basePath, "Directory.Build.props"),
                status,
                options.Force,
                exitCode,
                candidates,
                summary,
                backup,
                VerificationPayload.From(verification, options.VerifyStrict)
            ),
            options,
            _consoleService
        );
    }

    /// <summary>
    /// Re-restores the unified tree and answers whether the resolved graph moved only in ways the
    /// pass claimed — the <c>PackageReference</c> items hoisted into Directory.Build.props. Anything
    /// else is unexplained drift and the pass is rolled back from its own backup.
    /// </summary>
    private async Task<VerificationReport> VerifyUnifiedTreeAsync(
        Options options,
        List<string> projectPaths,
        string basePath,
        GraphSnapshotResult baseline,
        List<ItemCandidate> itemCandidates,
        FixBackupSession? backupSession,
        MigrationVerifier verifier
    )
    {
        _consoleService.Info("Verifying the resolved dependency graph (dotnet restore)...");

        var after = await verifier.CaptureAsync(
            MigrationService.RestoreTarget(options),
            projectPaths,
            basePath
        );

        VerificationReport report;
        if (!after.RestoreSucceeded)
        {
            // The loudest outcome and the one the flag exists for: unification produced a tree
            // that does not restore.
            report = new VerificationReport(
                VerificationVerdict.Failed,
                ProjectsRestored: 0,
                baseline.Snapshot.ProjectCount,
                ResolvedVersionCount: 0,
                UnchangedCount: 0,
                [],
                [],
                [],
                $"the solution does not restore after unification. {Tail(after.RestoreOutput)}"
            );
        }
        else
        {
            var diff = GraphDiff.Compare(baseline.Snapshot, after.Snapshot);

            if (diff.IntegrityFailures.Count > 0)
            {
                report = new VerificationReport(
                    VerificationVerdict.Failed,
                    after.Snapshot.Projects.Count,
                    baseline.Snapshot.ProjectCount,
                    after.Snapshot.ResolvedVersionCount,
                    diff.UnchangedCount,
                    [],
                    diff.IntegrityFailures,
                    [],
                    "the graph before and after unification do not cover the same projects, so "
                        + "they cannot be compared"
                );
            }
            else
            {
                // The unified PackageReference items are the run's claims: a package no candidate
                // named that nonetheless moved is unexplained drift, and rolls back the same way.
                var unifiedPackageIds = itemCandidates
                    .Where(c => c.Item.ItemType == "PackageReference")
                    .Select(c => c.Item.Include)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var attributed = DriftAttributor.AttributeUnification(
                    diff.Changes,
                    unifiedPackageIds,
                    baseline.Snapshot,
                    after.Snapshot
                );

                var hasUnexplained = attributed.Any(c => c.Kind == DriftExplanation.Unexplained);
                var verdict = (attributed.Count, hasUnexplained) switch
                {
                    (0, _) => VerificationVerdict.Unchanged,
                    (_, true) => VerificationVerdict.UnexplainedDrift,
                    _ => VerificationVerdict.ExplainedDrift,
                };

                report = new VerificationReport(
                    verdict,
                    after.Snapshot.Projects.Count,
                    baseline.Snapshot.ProjectCount,
                    after.Snapshot.ResolvedVersionCount,
                    diff.UnchangedCount,
                    attributed,
                    [],
                    [],
                    FailureReason: hasUnexplained
                        ? "the resolved graph moved in ways the unification does not account for"
                        : null
                );
            }
        }

        _display.ShowVerificationReport(report, options.VerifyStrict, options.Quiet);

        if (!report.ShouldRollBack)
        {
            return report;
        }

        // Same judgment the migration's verify-rollback keeps: --verify is itself the consent to
        // be protected from a change nobody has read, and a prompt a CI run cannot answer would
        // leave the breakage on disk while the report claimed otherwise.
        var rolledBack = await RollBackUnverifiedUnifyAsync(options, backupSession);
        return report with { RolledBack = rolledBack };
    }

    /// <summary>
    /// Undoes a unify pass the verification could not vouch for, from the backup the pass made.
    /// </summary>
    private async Task<bool> RollBackUnverifiedUnifyAsync(
        Options options,
        FixBackupSession? backupSession
    )
    {
        if (_rollbackHandler is null || backupSession is not { ManifestWritten: true })
        {
            _consoleService.Warning(
                "No backup is available, so the unification could not be undone. The working tree "
                    + "still holds changes this run could not verify — use git to discard them."
            );
            return false;
        }

        _consoleService.Warning("Rolling the unification back.");

        // The backup path names the .cpmigrate_backup directory itself; --rollback expects its
        // parent — the same resolution BackupCoordinator keeps for the migration path.
        var backupDir = Path.GetDirectoryName(
            backupSession.BackupPath!.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            )
        );

        var rollbackOptions = new Options
        {
            BackupDir = string.IsNullOrEmpty(backupDir) ? options.BackupDir : backupDir,
            Rollback = true,
            Force = true,
            Output = options.Output,
            Quiet = options.Quiet,
        };

        var result = await _rollbackHandler.ExecuteAsync(rollbackOptions);
        return result.ExitCode == ExitCodes.Success;
    }

    /// <summary>The last few lines of a restore log — the NU error that matters is at the end.</summary>
    private static string Tail(string output)
    {
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(5)
            .ToList();

        return lines.Count == 0 ? string.Empty : string.Join(" ", lines);
    }

    private static double GetConsensusThreshold(int totalProjects) =>
        Math.Ceiling(totalProjects * ConsensusThresholdPercent);

    private static List<PropertyCandidate> FindPropertyCandidates(PropertyAnalysisResult analysis, double threshold)
    {
        return analysis.PropertyOccurrences
            .GroupBy(kv => kv.Value[0].Name)
            .Select(g =>
            {
                var mostCommon = g.MaxBy(kv => kv.Value.Count);
                return new PropertyCandidate(mostCommon.Value[0], mostCommon.Value.Count);
            })
            .Where(x => x.Count >= threshold)
            .OrderBy(x => x.Property.Name)
            .ToList();
    }

    private static List<ItemCandidate> FindItemCandidates(PropertyAnalysisResult analysis, double threshold)
    {
        return analysis.ItemOccurrences
            .GroupBy(kv => $"{kv.Value[0].ItemType}|{kv.Value[0].Include}")
            .Select(g =>
            {
                var mostCommon = g.MaxBy(kv => kv.Value.Count);
                return new ItemCandidate(mostCommon.Value[0], mostCommon.Value.Count);
            })
            .Where(x => x.Count >= threshold)
            .OrderBy(x => x.Item.ItemType).ThenBy(x => x.Item.Include)
            .ToList();
    }

    private void DisplayCandidates(List<PropertyCandidate> propertyCandidates, List<ItemCandidate> itemCandidates, PropertyAnalysisResult analysis)
    {
        var totalProjects = analysis.TotalProjects;
        if (propertyCandidates.Count > 0)
        {
            _consoleService.Info($"Found {propertyCandidates.Count} common properties (consensus > {ConsensusThresholdPercent:P0}):");
            foreach (var candidate in propertyCandidates)
            {
                var percentage = (double)candidate.Count / totalProjects * 100;
                var gains = totalProjects - candidate.Count;
                var gainNote = gains > 0
                    ? $" [yellow]({gains} project(s) will newly receive it)[/]"
                    : "";
                _consoleService.Dim($"  - {candidate.Property.Name} = {candidate.Property.Value} [green]({candidate.Count}/{totalProjects}, {percentage:F0}%)[/]{gainNote}");
            }
        }

        if (itemCandidates.Count > 0)
        {
            _consoleService.Info($"Found {itemCandidates.Count} common items (consensus > {ConsensusThresholdPercent:P0}):");
            foreach (var candidate in itemCandidates)
            {
                var percentage = (double)candidate.Count / totalProjects * 100;
                var meta = candidate.Item.Metadata != null && candidate.Item.Metadata.Count > 0
                    ? $" ({string.Join(", ", candidate.Item.Metadata.Select(m => $"{m.Key}={m.Value}"))})"
                    : "";
                var gains = totalProjects - candidate.Count;
                var gainNote = gains > 0
                    ? $" [yellow]({gains} project(s) will newly receive it)[/]"
                    : "";
                _consoleService.Dim($"  - [{candidate.Item.ItemType}] {candidate.Item.Include}{meta} [green]({candidate.Count}/{totalProjects}, {percentage:F0}%)[/]{gainNote}");

                WarnOnVariantHolders(candidate, analysis);
            }
        }
    }

    /// <summary>
    /// A project that holds the same item under different metadata keeps it — only exact-metadata
    /// matches are stripped — and after unification it sees both its own and the props-level copy.
    /// For a PackageReference that duplicate is a NU1504 warning on every restore; say so before
    /// the write rather than leaving it to be discovered as a phantom build warning.
    /// </summary>
    private void WarnOnVariantHolders(ItemCandidate candidate, PropertyAnalysisResult analysis)
    {
        var prefix = $"{candidate.Item.ItemType}|{candidate.Item.Include}|";
        var variantProjects = analysis.ItemOccurrences
            .Where(kv =>
                kv.Key.StartsWith(prefix, StringComparison.Ordinal)
                && kv.Key != CreateLookupKey(candidate.Item))
            .SelectMany(kv => kv.Value)
            .Select(i => i.ProjectPath);

        // Conditional holders keep their item too — it was never a consensus member, so nothing
        // strips it — and when their condition holds they see both copies.
        var conditionalProjects = analysis.ConditionalItems
            .Where(i =>
                string.Equals(i.ItemType, candidate.Item.ItemType, StringComparison.Ordinal)
                && string.Equals(i.Include, candidate.Item.Include, StringComparison.Ordinal))
            .Select(i => i.ProjectPath);

        var holders = variantProjects
            .Concat(conditionalProjects)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (holders.Count == 0)
        {
            return;
        }

        _consoleService.Warning(
            $"  {holders.Count} project(s) keep their own {candidate.Item.ItemType} "
                + $"{candidate.Item.Include} under different metadata or a condition — after "
                + "unification they will see both, which NuGet reports as a duplicate item."
        );
    }

    private static string CreateLookupKey(Models.ProjectItem item) =>
        $"{item.ItemType}|{item.Include}|{string.Join(";", (item.Metadata ?? new Dictionary<string, string>()).OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}"))}";

    private sealed record PropertyCandidate(CPMigrate.Models.ProjectProperty Property, int Count);

    private sealed record ItemCandidate(CPMigrate.Models.ProjectItem Item, int Count);

    private async Task CreateOrUpdateBuildProps(string path,
        List<CPMigrate.Models.ProjectProperty> properties,
        List<CPMigrate.Models.ProjectItem> items,
        FixBackupSession? backupSession)
    {
        using var collection = new ProjectCollection();
        ProjectRootElement root;
        backupSession?.BeforeWrite(path);
        if (File.Exists(path))
        {
            _consoleService.Info($"Updating existing {Path.GetFileName(path)}...");
            root = ProjectRootElement.Open(path, collection);
        }
        else
        {
            _consoleService.Info($"Creating new {Path.GetFileName(path)}...");
            root = ProjectRootElement.Create(collection);
        }

        // Add Properties
        if (properties.Count > 0)
        {
            var propertyGroup = root.PropertyGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition));
            if (propertyGroup == null)
            {
                propertyGroup = root.AddPropertyGroup();
            }

            foreach (var prop in properties)
            {
                var existing = propertyGroup.Properties.FirstOrDefault(p => p.Name == prop.Name);
                if (existing != null)
                {
                    existing.Value = prop.Value;
                }
                else
                {
                    propertyGroup.AddProperty(prop.Name, prop.Value);
                }
            }
        }

        // Add Items
        if (items.Count > 0)
        {
            var itemGroup = root.ItemGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition));
            if (itemGroup == null)
            {
                itemGroup = root.AddItemGroup();
            }

            foreach (var item in items)
            {
                // Check if exists (simplified check by Include)
                var existing = itemGroup.Items.FirstOrDefault(i => i.ItemType == item.ItemType && i.Include == item.Include);
                if (existing != null)
                {
                    // Remove existing to refresh metadata
                    itemGroup.RemoveChild(existing);
                }

                var newItem = itemGroup.AddItem(item.ItemType, item.Include);
                if (item.Metadata != null)
                {
                    foreach (var m in item.Metadata)
                    {
                        newItem.AddMetadata(m.Key, m.Value);
                    }
                }
            }
        }

        root.Save(path);
    }

    /// <returns>How many project files were actually rewritten — the honest count the report owes.</returns>
    private async Task<int> RemoveItemsFromProjects(List<string> projectPaths, List<CPMigrate.Models.ProjectItem> itemsToRemove, FixBackupSession? backupSession)
    {
        var modifiedCount = 0;
        if (itemsToRemove.Count == 0)
        {
            return modifiedCount;
        }

        // Lookup: Type|Include -> Metadata
        var targetItems = itemsToRemove.ToDictionary(
            i => $"{i.ItemType}|{i.Include}",
            i => i.Metadata
        );

        foreach (var projectPath in projectPaths)
        {
            var modified = ProcessProjectForItemRemoval(projectPath, targetItems, backupSession);

            if (modified)
            {
                modifiedCount++;
                _consoleService.Dim($"Updated {Path.GetFileName(projectPath)}");
            }
        }

        return modifiedCount;
    }

    private bool ProcessProjectForItemRemoval(
        string projectPath,
        Dictionary<string, Dictionary<string, string>?> targetItems,
        FixBackupSession? backupSession)
    {
        using var collection = new ProjectCollection();
        var root = ProjectRootElement.Open(projectPath, collection);
        var modified = false;

        foreach (var group in root.ItemGroups)
        {
            var items = group.Items
                .Where(i => targetItems.ContainsKey($"{i.ItemType}|{i.Include}"))
                .ToList();

            foreach (var item in items)
            {
                modified = TryRemoveItemIfMatches(item, targetItems, group, projectPath) || modified;
            }
        }

        // Remove empty item groups
        modified = RemoveEmptyItemGroups(root) || modified;

        if (modified)
        {
            backupSession?.BeforeWrite(projectPath);
            root.Save(projectPath);
        }

        return modified;
    }

    private bool TryRemoveItemIfMatches(
        ProjectItemElement item,
        Dictionary<string, Dictionary<string, string>?> targetItems,
        ProjectItemGroupElement group,
        string projectPath)
    {
        var key = $"{item.ItemType}|{item.Include}";
        var targetMetadata = targetItems[key];
        var itemMetadata = item.Metadata.ToDictionary(m => m.Name, m => m.Value);

        if (!MetadataMatches(itemMetadata, targetMetadata))
        {
            _consoleService.Warning(
                $"Skipped removing item '{item.ItemType} {item.Include}' in {Path.GetFileName(projectPath)}: Metadata mismatch.");
            return false;
        }

        group.RemoveChild(item);
        return true;
    }

    private static bool MetadataMatches(
        Dictionary<string, string> itemMetadata,
        Dictionary<string, string>? targetMetadata)
    {
        // If target has no metadata, item must also have none
        if (targetMetadata == null)
        {
            return itemMetadata.Count == 0;
        }

        // Count must match
        if (itemMetadata.Count != targetMetadata.Count)
        {
            return false;
        }

        // All target metadata must exist with matching values
        return targetMetadata.All(tm =>
            itemMetadata.TryGetValue(tm.Key, out var val) && val == tm.Value);
    }

    private static bool RemoveEmptyItemGroups(ProjectRootElement root)
    {
        var emptyGroups = root.ItemGroups
            .Where(g => g.Count == 0 && string.IsNullOrEmpty(g.Condition))
            .ToList();

        foreach (var group in emptyGroups)
        {
            root.RemoveChild(group);
        }

        return emptyGroups.Count > 0;
    }

    /// <returns>How many project files were actually rewritten — the honest count the report owes.</returns>
    private async Task<int> RemovePropertiesFromProjects(List<string> projectPaths, List<CPMigrate.Models.ProjectProperty> propertiesToRemove, FixBackupSession? backupSession)
    {
        var modifiedCount = 0;
        if (propertiesToRemove.Count == 0)
        {
            return modifiedCount;
        }

        var propertiesSet = new HashSet<string>(propertiesToRemove.Select(p => p.Name));

        foreach (var projectPath in projectPaths)
        {
            // Use a local collection to ensure no caching issues
            using var collection = new ProjectCollection();
            var root = ProjectRootElement.Open(projectPath, collection);
            var modified = false;

            foreach (var group in root.PropertyGroups)
            {
                // ToList to allow modification during iteration
                var props = group.Properties.Where(p => propertiesSet.Contains(p.Name)).ToList();
                foreach (var prop in props)
                {
                    // Only remove if value matches (defensive, though our analysis said they all match)
                    var targetValue = propertiesToRemove.First(p => p.Name == prop.Name).Value;
                    if (prop.Value == targetValue)
                    {
                        group.RemoveChild(prop);
                        modified = true;
                    }
                    else
                    {
                        // Explicitly log why we aren't removing it, to help the user debug
                        _consoleService.Warning($"Skipped removing '{prop.Name}' in {Path.GetFileName(projectPath)}: Value mismatch.");
                        _consoleService.Dim($"  Expected: '{targetValue}'");
                        _consoleService.Dim($"  Found:    '{prop.Value}'");
                    }
                }
            }

            // Remove empty property groups
            var emptyGroups = root.PropertyGroups.Where(g => g.Count == 0 && string.IsNullOrEmpty(g.Condition)).ToList();
            foreach (var group in emptyGroups)
            {
                root.RemoveChild(group);
                modified = true;
            }

            if (modified)
            {
                modifiedCount++;
                backupSession?.BeforeWrite(projectPath);
                root.Save(projectPath);
                _consoleService.Dim($"Updated {Path.GetFileName(projectPath)}");
            }
        }

        return modifiedCount;
    }
}
