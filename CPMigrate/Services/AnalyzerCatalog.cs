using CPMigrate.Analyzers;
using Microsoft.Extensions.Logging;

namespace CPMigrate.Services;

internal static class AnalyzerCatalog
{
    public static IReadOnlyList<IAnalyzer> CreateDefault(IConsoleService consoleService, ILoggerFactory? loggerFactory = null)
    {
        var projectFileScanner = new ProjectFileScanner(consoleService, loggerFactory?.CreateLogger<ProjectFileScanner>());
        var dependencyGraphService = new DependencyGraphService(consoleService);

        return
        [
            new VersionInconsistencyAnalyzer(),
            new DuplicatePackageAnalyzer(),
            new RedundantReferenceAnalyzer(),
            new TransitiveDependencyAnalyzer(),
            new VulnerabilityAnalyzer(),
            new OutdatedPackageAnalyzer(),
            new DeprecatedPackageAnalyzer(),
            new LiftingAnalyzer(dependencyGraphService),
            new FrameworkAlignmentAnalyzer(projectFileScanner)
        ];
    }
}
