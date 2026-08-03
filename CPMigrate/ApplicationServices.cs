using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Verify;
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
        ILoggerFactory? loggerFactory = null
    )
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

    /// <summary>
    /// Returns a new <see cref="ApplicationServices"/> wired with <paramref name="console"/> instead of the
    /// current console, while preserving the logger factory. Used by <see cref="CommandRouter"/> to swap in
    /// <see cref="SilentConsoleService"/> under <c>--output Json</c> so dependency-discovery notices
    /// ("Found project: …") never leak into the JSON-only stdout contract.
    /// </summary>
    internal ApplicationServices WithConsole(IConsoleService console)
    {
        var solutionDiscovery = new SolutionDiscovery(console);
        var projectFileScanner = new ProjectFileScanner(
            console,
            _loggerFactory?.CreateLogger<ProjectFileScanner>()
        );
        var packageQueryService = new DotNetPackageQueryService(console);
        var projectAnalyzer = new ProjectAnalyzer(
            console,
            solutionDiscovery,
            projectFileScanner,
            packageQueryService,
            _loggerFactory?.CreateLogger<ProjectAnalyzer>()
        );
        var interactiveService = new InteractiveService(
            console,
            solutionDiscovery: solutionDiscovery
        );
        var configService = new ConfigService(console);

        return new ApplicationServices(
            console,
            interactiveService,
            VersionResolver,
            configService,
            BackupManager,
            projectAnalyzer,
            _loggerFactory
        );
    }

    public static ApplicationServices Create(
        IConsoleService? customConsole = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        var versionResolver = new VersionResolver(null);
        var consoleService = customConsole ?? new SpectreConsoleService(versionResolver);
        var solutionDiscovery = new SolutionDiscovery(consoleService);
        var projectFileScanner = new ProjectFileScanner(
            consoleService,
            loggerFactory?.CreateLogger<ProjectFileScanner>()
        );
        var dotNetQueryService = new DotNetPackageQueryService(consoleService);
        var projectAnalyzer = new ProjectAnalyzer(
            consoleService,
            solutionDiscovery,
            projectFileScanner,
            dotNetQueryService,
            loggerFactory?.CreateLogger<ProjectAnalyzer>()
        );
        var interactiveService = new InteractiveService(
            consoleService,
            solutionDiscovery: solutionDiscovery
        );
        var configService = new ConfigService(consoleService);
        var backupManager = new BackupManager();

        return new ApplicationServices(
            consoleService,
            interactiveService,
            versionResolver,
            configService,
            backupManager,
            projectAnalyzer,
            loggerFactory
        );
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
            _loggerFactory?.CreateLogger<MigrationService>(),
            new MigrationVerifier(
                new AssetsGraphSnapshotService(
                    new DotNetCliService(),
                    new DependencyGraphService(ConsoleService),
                    ConsoleService
                )
            )
        );
    }

    public PackageUpdateService CreatePackageUpdateService()
    {
        return new PackageUpdateService(
            ConsoleService,
            ProjectAnalyzer,
            new PropsGenerator(VersionResolver),
            new NuGetVersionLookupService(
                logger: _loggerFactory?.CreateLogger<NuGetVersionLookupService>()
            ),
            new DotNetCliService(),
            BackupManager,
            _loggerFactory?.CreateLogger<PackageUpdateService>()
        );
    }

    public UpdateService CreateUpdateService()
    {
        return new UpdateService(
            ConsoleService,
            logger: _loggerFactory?.CreateLogger<UpdateService>()
        );
    }

    public BuildPropsService CreateBuildPropsService()
    {
        return new BuildPropsService(ConsoleService, ProjectAnalyzer);
    }
}
