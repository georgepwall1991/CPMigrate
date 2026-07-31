using CPMigrate.Analyzers;
using Microsoft.Extensions.Logging;

namespace CPMigrate.Services;

internal static class AnalyzerCatalog
{
    public static IReadOnlyList<IAnalyzer> CreateDefault(
        IConsoleService consoleService,
        ILoggerFactory? loggerFactory = null
    )
    {
        var projectFileScanner = new ProjectFileScanner(
            consoleService,
            loggerFactory?.CreateLogger<ProjectFileScanner>()
        );
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
            new FrameworkAlignmentAnalyzer(projectFileScanner),
            // Gated on data, not a flag, like the other analyzers: it reports nothing unless the
            // solution actually has a Directory.Packages.props to drift from.
            new CpmDriftAnalyzer(),
            new LicenseAnalyzer(),
            // Reads declared XML and the props file, never the resolved graph: resolution has
            // already turned a wildcard into a concrete version by the time it reaches an analyzer.
            new FloatingVersionAnalyzer(),
        ];
    }
}
