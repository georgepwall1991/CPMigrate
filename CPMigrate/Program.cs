namespace CPMigrate;

/// <summary>
/// Process entry point. Explicit <c>Main</c> rather than top-level statements so Stryker
/// can compile mutants of this assembly without CS8805.
/// </summary>
internal static class Program
{
    // Stryker disable all : the entry point has no branching worth mutating
    private static Task<int> Main(string[] args) => ProgramRunner.RunAsync(args);
}
