using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// No-op console implementation used when strict machine-readable output is required.
/// </summary>
public sealed class SilentConsoleService : IConsoleService
{
    public static SilentConsoleService Instance { get; } = new();

    private SilentConsoleService()
    {
    }

    /// <summary>Never interactive — this implementation exists to keep the stream machine-readable.</summary>
    public bool IsInteractive => false;

    public void Info(string message)
    {
    }

    public void Success(string message)
    {
    }

    public void Warning(string message)
    {
    }

    public void Error(string message)
    {
    }

    public void Highlight(string message)
    {
    }

    public void Dim(string message)
    {
    }

    public void DryRun(string message)
    {
    }

    public void WriteHeader()
    {
    }

    public void Banner(string message)
    {
    }

    public void Separator()
    {
    }

    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions, List<string> conflicts, ConflictStrategy strategy)
    {
    }

    public void WriteSummaryTable(int projectCount, int packageCount, int conflictCount, string propsFilePath, string? backupPath, bool wasDryRun)
    {
    }

    public void WriteProjectTree(List<string> projectPaths, string basePath)
    {
    }

    public void WritePropsPreview(string content)
    {
    }

    public void WriteDiff(string diff)
    {
    }

    public void WriteMarkup(string message)
    {
    }

    public void WriteLine(string message = "")
    {
    }

    public void WriteStatusDashboard(string directory, List<string> solutions, List<BackupSetInfo> backups, bool isGitRepo, bool hasUnstaged, Dictionary<string, int> targetFrameworks)
    {
    }

    public void WriteMissionStatus(int step)
    {
    }

    public void WriteRiskScore(int conflictCount, int projectCount)
    {
    }

    public string AskSelection(string title, IEnumerable<string> choices)
    {
        return string.Empty;
    }

    public string AskGroupedSelection(string title, Dictionary<string, IEnumerable<string>> groups)
    {
        return string.Empty;
    }

    public bool AskConfirmation(string message)
    {
        return false;
    }

    public string AskText(string prompt, string defaultValue = "")
    {
        return defaultValue;
    }

    public int AskInt(string prompt, int defaultValue)
    {
        return defaultValue;
    }

    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath)
    {
    }

    public void WriteAnalysisHeader(int projectCount, int packageCount, int vulnerabilityCount)
    {
    }

    public void WriteAnalyzerResult(AnalyzerResult result)
    {
    }

    public void WriteAnalysisSummary(AnalysisReport report)
    {
    }
}
