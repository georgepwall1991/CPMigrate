using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Services.Update;

namespace CPMigrate.Models;

public sealed record BackupSettings(
    bool Enabled,
    string BackupDir,
    bool AddBackupToGitignore,
    string GitignoreDir)
{
    public static BackupSettings FromOptions(Options options) =>
        new(
            Enabled: !options.NoBackup,
            BackupDir: options.BackupDir,
            AddBackupToGitignore: options.AddBackupToGitignore,
            GitignoreDir: options.GitignoreDir);
}

public sealed record CommandOutput(
    OutputFormat Format,
    bool Quiet,
    bool Force,
    string? OutputFile)
{
    public bool IsJson => Format == OutputFormat.Json;
    public bool IsNonInteractive => Quiet || IsJson;

    public static CommandOutput FromOptions(Options options) =>
        new(options.Output, options.Quiet, options.Force, options.OutputFile);
}

public sealed record MigrationRequest(
    string DiscoveryTargetPath,
    string? ProjectPath,
    string OutputDir,
    bool KeepVersionAttributes,
    bool DryRun,
    bool MergeExisting,
    bool IncludeTransitive,
    bool InteractiveConflicts,
    ConflictStrategy ConflictStrategy,
    BackupSettings Backup,
    CommandOutput Output)
{
    public bool HasExplicitProjectPath => !string.IsNullOrWhiteSpace(ProjectPath);

    public static MigrationRequest FromOptions(Options options) =>
        new(
            DiscoveryTargetPath: options.GetDiscoveryTargetPath(),
            ProjectPath: options.HasExplicitProjectPath ? options.ProjectFileDir : null,
            OutputDir: options.OutputDir,
            KeepVersionAttributes: options.KeepAttributes,
            DryRun: options.DryRun,
            MergeExisting: options.MergeExisting,
            IncludeTransitive: options.IncludeTransitive,
            InteractiveConflicts: options.InteractiveConflicts,
            ConflictStrategy: options.ConflictStrategy,
            Backup: BackupSettings.FromOptions(options),
            Output: CommandOutput.FromOptions(options));
}

public sealed record AnalysisRequest(
    string DiscoveryTargetPath,
    string? ProjectPath,
    bool IncludeTransitive,
    bool AuditSecurity,
    bool AnalyzeOutdated,
    bool AnalyzeDeprecated,
    bool IncludePrerelease,
    FixRequest? Fix,
    CommandOutput Output)
{
    public bool HasExplicitProjectPath => !string.IsNullOrWhiteSpace(ProjectPath);

    public static AnalysisRequest FromOptions(Options options) =>
        new(
            DiscoveryTargetPath: options.GetDiscoveryTargetPath(),
            ProjectPath: options.HasExplicitProjectPath ? options.ProjectFileDir : null,
            IncludeTransitive: options.IncludeTransitive,
            AuditSecurity: options.AuditSecurity,
            AnalyzeOutdated: options.AnalyzeOutdated,
            AnalyzeDeprecated: options.AnalyzeDeprecated,
            IncludePrerelease: options.IncludePrerelease,
            Fix: options.Fix || options.FixDryRun ? FixRequest.FromOptions(options) : null,
            Output: CommandOutput.FromOptions(options));
}

public sealed record FixRequest(
    string PropsFilePath,
    ConflictStrategy ConflictStrategy,
    bool DryRun)
{
    public static FixRequest FromOptions(Options options)
    {
        var (_, propsPath) = MigrationValidator.GetOutputPaths(options);
        return new(propsPath, options.ConflictStrategy, options.FixDryRun);
    }
}

public sealed record RollbackRequest(
    BackupSettings Backup,
    CommandOutput Output)
{
    public static RollbackRequest FromOptions(Options options) =>
        new(BackupSettings.FromOptions(options), CommandOutput.FromOptions(options));
}

public sealed record ListBackupsRequest(
    string BackupDir,
    CommandOutput Output)
{
    public static ListBackupsRequest FromOptions(Options options) =>
        new(options.BackupDir, CommandOutput.FromOptions(options));
}

public sealed record PackageUpdateRequest(
    string SolutionPath,
    bool IncludePrerelease,
    bool IncludeTransitive,
    bool DryRun,
    BackupSettings Backup,
    CommandOutput Output,
    bool Bisect = false,
    int BisectBudget = BisectSearchStrategy.DefaultBudget,
    string? BisectTestFilter = null,
    IReadOnlyList<string>? OnlyPackages = null)
{
    public static PackageUpdateRequest FromOptions(Options options) =>
        new(
            SolutionPath: options.SolutionFileDir,
            IncludePrerelease: options.IncludePrerelease,
            IncludeTransitive: options.IncludeTransitive,
            DryRun: options.DryRun,
            Backup: BackupSettings.FromOptions(options),
            Output: CommandOutput.FromOptions(options),
            Bisect: options.Bisect,
            BisectBudget: options.EffectiveBisectBudget,
            BisectTestFilter: options.BisectTestFilter,
            OnlyPackages: options.ParseOnlyPackages());
}

public sealed record BatchRequest(
    string BatchDir,
    bool Analyze,
    bool DryRun,
    bool Parallel,
    bool ContinueOnFailure,
    CommandOutput Output)
{
    public static BatchRequest FromOptions(Options options) =>
        new(
            BatchDir: options.BatchDir ?? string.Empty,
            Analyze: options.Analyze,
            DryRun: options.DryRun,
            Parallel: options.BatchParallel,
            ContinueOnFailure: options.BatchContinue,
            Output: CommandOutput.FromOptions(options));
}
