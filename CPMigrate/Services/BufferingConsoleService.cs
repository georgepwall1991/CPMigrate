using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Collects what a scan would have printed, instead of printing it.
///
/// <para>
/// Concurrent scans must not write to the shared terminal: lines would follow completion order
/// rather than discovery order, and <see cref="IConsoleService"/> carries no thread-safety
/// contract. A scan runs against one of these; the caller replays <see cref="Warnings"/> at the
/// point in discovery order where the sequential path would have emitted them. Everything else a
/// scan might reach goes nowhere — scans only ever report through <c>Warning</c>.
/// </para>
/// </summary>
public sealed class BufferingConsoleService : IConsoleService
{
    private readonly object _lock = new();
    private readonly List<string> _warnings = [];

    /// <summary>What the scan tried to print, in the order it tried to print it.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_lock)
            {
                return [.. _warnings];
            }
        }
    }

    /// <summary>Replays every buffered warning into <paramref name="target"/>, in order.</summary>
    public void ReplayInto(IConsoleService target)
    {
        foreach (var warning in Warnings)
        {
            target.Warning(warning);
        }
    }

    public bool IsInteractive => false;

    public void Info(string message) { }

    public void Success(string message) { }

    public void Warning(string message)
    {
        lock (_lock)
        {
            _warnings.Add(message);
        }
    }

    public void Error(string message) { }

    public void Highlight(string message) { }

    public void Dim(string message) { }

    public void DryRun(string message) { }

    public void WriteHeader() { }

    public void Banner(string message) { }

    public void Separator() { }

    public void WriteConflictsTable(
        Dictionary<string, HashSet<string>> packageVersions,
        List<string> conflicts,
        ConflictStrategy strategy
    ) { }

    public void WriteSummaryTable(
        int projectCount,
        int packageCount,
        int conflictCount,
        string propsFilePath,
        string? backupPath,
        bool wasDryRun
    ) { }

    public void WriteProjectTree(List<string> projectPaths, string basePath) { }

    public void WritePropsPreview(string content) { }

    public void WriteDiff(string diff) { }

    public void WriteStructuredError(
        string title,
        string detail,
        string? suggestion = null,
        string? docsUrl = null
    ) { }

    public void WriteMarkup(string message) { }

    public void WriteLine(string message = "") { }

    public void WriteStatusDashboard(
        string directory,
        List<string> solutions,
        List<BackupSetInfo> backups,
        bool isGitRepo,
        bool hasUnstaged,
        Dictionary<string, int> targetFrameworks
    ) { }

    public void WriteMissionStatus(int step) { }

    public void WriteRiskScore(int conflictCount, int projectCount) { }

    public string AskSelection(string title, IEnumerable<string> choices) =>
        throw new InvalidOperationException("A buffered scan never prompts.");

    public string AskGroupedSelection(string title, Dictionary<string, IEnumerable<string>> groups) =>
        throw new InvalidOperationException("A buffered scan never prompts.");

    public bool AskConfirmation(string message) =>
        throw new InvalidOperationException("A buffered scan never prompts.");

    public string AskText(string prompt, string defaultValue = "") =>
        throw new InvalidOperationException("A buffered scan never prompts.");

    public int AskInt(string prompt, int defaultValue) =>
        throw new InvalidOperationException("A buffered scan never prompts.");

    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath) { }

    public void WriteAnalysisHeader(int projectCount, int packageCount, int vulnerabilityCount) { }

    public void WriteAnalyzerResult(AnalyzerResult result) { }

    public void WriteAnalysisSummary(AnalysisReport report) { }
}
