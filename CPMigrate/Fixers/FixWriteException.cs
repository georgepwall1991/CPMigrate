namespace CPMigrate.Fixers;

/// <summary>
/// A fixer could not read or write a project file it was asked to change.
///
/// Exists so that "there was nothing to change" and "I could not change it" stop being the same outcome.
/// Both fixers that edit project XML used to swallow the exception and return null, which the caller could
/// only interpret as the former — so <c>--fix</c> on a read-only or locked file reported
/// <em>"No changes were needed"</em> over a finding it had just called fixable. The gate still caught the
/// unrepaired finding on the post-fix rescan, so nothing shipped broken; but the user was told the opposite
/// of what happened, and the actual cause — a permission problem, a lock, malformed XML — was thrown away.
/// </summary>
public sealed class FixWriteException : Exception
{
    public FixWriteException(string projectPath, Exception inner)
        : base($"Could not modify {Path.GetFileName(projectPath)}: {inner.Message}", inner)
    {
        ProjectPath = projectPath;
    }

    /// <summary>The file that could not be changed.</summary>
    public string ProjectPath { get; }
}
