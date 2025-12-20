using CPMigrate.Models;
using CPMigrate.Services;

namespace CPMigrate.Tests.TestDoubles;

public class FakeConsoleService : IConsoleService
{
    public bool ConfirmationResponse { get; set; } = true;
    public Queue<string> TextResponses { get; set; } = new();
    public Queue<string> SelectionResponses { get; set; } = new();

    public void Info(string message) { }
    public void Success(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
    public void Highlight(string message) { }
    public void Dim(string message) { }
    public void DryRun(string message) { }
    public void WriteHeader() { }
    public void Banner(string message) { }
    public void Separator() { }
    public void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions, List<string> conflicts, ConflictStrategy strategy) { }
    public void WriteSummaryTable(int projectCount, int packageCount, int conflictCount, string propsFilePath, string? backupPath, bool wasDryRun) { }
    public void WriteProjectTree(List<string> projectPaths, string basePath) { }
    public void WritePropsPreview(string content) { }
    public void WriteMarkup(string message) { }
    public void WriteLine(string message = "") { }
    public void WriteStatusDashboard(string directory, List<string> solutions, List<BackupSetInfo> backups, bool isGitRepo, bool hasUnstaged, Dictionary<string, int> targetFrameworks) { }
    public List<int> MissionStatusSteps { get; } = new();
    public void WriteMissionStatus(int step) => MissionStatusSteps.Add(step);
    public void WriteRiskScore(int conflictCount, int projectCount) { }
    public string AskSelection(string title, IEnumerable<string> choices)
    {
        if (SelectionResponses.Count > 0)
            return SelectionResponses.Dequeue();
        return choices.First();
    }
    public string AskGroupedSelection(string title, Dictionary<string, IEnumerable<string>> groups)
    {
        if (SelectionResponses.Count > 0)
            return SelectionResponses.Dequeue();
        return groups.Values.First().First();
    }
    public bool AskConfirmation(string message) => ConfirmationResponse;
    public string AskText(string prompt, string defaultValue = "")
    {
        if (TextResponses.Count > 0)
            return TextResponses.Dequeue();
        return defaultValue;
    }
    public Queue<int> IntResponses { get; set; } = new();
    public int AskInt(string prompt, int defaultValue)
    {
        if (IntResponses.Count > 0)
            return IntResponses.Dequeue();
        return defaultValue;
    }
    public void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath) { }
    public void WriteAnalysisHeader(int projectCount, int packageCount, int vulnerabilityCount) { }
    public void WriteAnalyzerResult(AnalyzerResult result) { }
    public void WriteAnalysisSummary(AnalysisReport report) { }
}
