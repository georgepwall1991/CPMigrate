using CPMigrate.Models;
using CPMigrate.Services;
using Microsoft.Extensions.Logging;

namespace CPMigrate;

internal sealed class ApplicationServices
{
    private readonly ILoggerFactory? _loggerFactory;

    public ApplicationServices(
        IConsoleService consoleService,
        IInteractiveService interactiveService,
        VersionResolver versionResolver,
        ConfigService configService,
        IBackupManager backupManager,
        IProjectAnalyzer projectAnalyzer,
        ILoggerFactory? loggerFactory = null)
    {
        ConsoleService = consoleService;
        InteractiveService = interactiveService;
        VersionResolver = versionResolver;
        ConfigService = configService;
        BackupManager = backupManager;
        ProjectAnalyzer = projectAnalyzer;
        _loggerFactory = loggerFactory;
    }

    public IConsoleService ConsoleService { get; }
    public IInteractiveService InteractiveService { get; }
    public VersionResolver VersionResolver { get; }
    public ConfigService ConfigService { get; }
    public IBackupManager BackupManager { get; }
    public IProjectAnalyzer ProjectAnalyzer { get; }

    public static ApplicationServices Create(IConsoleService? customConsole = null, ILoggerFactory? loggerFactory = null)
    {
        var versionResolver = new VersionResolver(null);
        var consoleService = customConsole ?? new SpectreConsoleService(versionResolver);
        var solutionDiscovery = new SolutionDiscovery(consoleService);
        var projectFileScanner = new ProjectFileScanner(consoleService, loggerFactory?.CreateLogger<ProjectFileScanner>());
        var dotNetQueryService = new DotNetPackageQueryService(
            consoleService);
        var projectAnalyzer = new ProjectAnalyzer(
            consoleService,
            solutionDiscovery,
            projectFileScanner,
            dotNetQueryService,
            loggerFactory?.CreateLogger<ProjectAnalyzer>());
        var interactiveService = new InteractiveService(consoleService, solutionDiscovery: solutionDiscovery);
        var configService = new ConfigService(consoleService);
        var backupManager = new BackupManager();

        return new ApplicationServices(
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            projectAnalyzer,
            loggerFactory);
    }

    public MigrationService CreateMigrationService(bool quietMode)
    {
        return new MigrationService(
            ConsoleService,
            ProjectAnalyzer,
            VersionResolver,
            new PropsGenerator(VersionResolver),
            BackupManager,
            new AnalysisService(AnalyzerCatalog.CreateDefault(ConsoleService, _loggerFactory)),
            new FixService(ConsoleService, FixerCatalog.CreateDefault(VersionResolver)),
            quietMode,
            _loggerFactory?.CreateLogger<MigrationService>());
    }

    public PackageUpdateService CreatePackageUpdateService()
    {
        return new PackageUpdateService(
            ConsoleService,
            ProjectAnalyzer,
            new PropsGenerator(VersionResolver),
            new NuGetVersionLookupService(logger: _loggerFactory?.CreateLogger<NuGetVersionLookupService>()),
            new DotNetCliService(),
            BackupManager,
            _loggerFactory?.CreateLogger<PackageUpdateService>());
    }

    public UpdateService CreateUpdateService()
    {
        return new UpdateService(ConsoleService, logger: _loggerFactory?.CreateLogger<UpdateService>());
    }

    public BuildPropsService CreateBuildPropsService()
    {
        return new BuildPropsService(ConsoleService, ProjectAnalyzer);
    }
}
