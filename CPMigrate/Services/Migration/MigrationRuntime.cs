using CPMigrate.Fixers;
using CPMigrate.Models;
namespace CPMigrate.Services.Migration;

internal sealed class MigrationRuntime
{
    public MigrationRuntime(
        IProjectAnalyzer projectAnalyzer,
        VersionResolver versionResolver,
        PropsGenerator propsGenerator,
        IBackupManager backupManager,
        IConsoleService consoleService,
        IAnalysisService analysisService,
        IFixService fixService,
        MigrationValidator validator,
        MigrationDisplay display,
        bool quietMode)
    {
        ProjectAnalyzer = projectAnalyzer;
        VersionResolver = versionResolver;
        PropsGenerator = propsGenerator;
        BackupManager = backupManager;
        ConsoleService = consoleService;
        AnalysisService = analysisService;
        FixService = fixService;
        Validator = validator;
        Display = display;
        QuietMode = quietMode;
        ProgressReporter = new MigrationProgressReporter(quietMode);
        BackupCoordinator = new BackupCoordinator(backupManager, consoleService, quietMode);
    }

    public IProjectAnalyzer ProjectAnalyzer { get; }
    public VersionResolver VersionResolver { get; }
    public PropsGenerator PropsGenerator { get; }
    public IBackupManager BackupManager { get; }
    public IConsoleService ConsoleService { get; }
    public IAnalysisService AnalysisService { get; }
    public IFixService FixService { get; }
    public MigrationValidator Validator { get; }
    public MigrationDisplay Display { get; }
    public bool QuietMode { get; }
    public IMigrationProgressReporter ProgressReporter { get; }
    public BackupCoordinator BackupCoordinator { get; }
    public Dictionary<string, List<PackageReference>> CachedProjectScans { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}
