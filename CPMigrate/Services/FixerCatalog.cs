using CPMigrate.Fixers;

namespace CPMigrate.Services;

internal static class FixerCatalog
{
    public static IReadOnlyList<IFixer> CreateDefault(VersionResolver versionResolver)
    {
        return
        [
            new VersionInconsistencyFixer(),
            new DuplicatePackageFixer(),
            new RedundantReferenceFixer(),
            new TransitiveConflictFixer(versionResolver)
        ];
    }
}

