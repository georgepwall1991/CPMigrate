using CPMigrate.Models;

namespace CPMigrate.Services;

public interface IConsoleService
{
    void Info(string message);
    void Success(string message);
    void Warning(string message);
    void Error(string message);
    void Highlight(string message);
    void Dim(string message);
    void DryRun(string message);
    void WriteHeader();
    void Banner(string message);
    void Separator();
    void WriteConflictsTable(Dictionary<string, HashSet<string>> packageVersions, List<string> conflicts, ConflictStrategy strategy);
    void WriteSummaryTable(int projectCount, int packageCount, int conflictCount, string propsFilePath, string? backupPath, bool wasDryRun);
    void WriteProjectTree(List<string> projectPaths, string basePath);
    void WritePropsPreview(string content);
    void WriteMarkup(string message);
    void WriteLine(string message = "");
    void WriteStatusDashboard(string directory, List<string> solutions, List<BackupSetInfo> backups, bool isGitRepo, bool hasUnstaged);
    void WriteMissionStatus(int step);
    void WriteRiskScore(int conflictCount, int projectCount);
    string AskSelection(string title, IEnumerable<string> choices);
    bool AskConfirmation(string message);
    string AskText(string prompt, string defaultValue = "");
    int AskInt(string prompt, int defaultValue);
    void WriteRollbackPreview(IEnumerable<string> filesToRestore, string? propsFilePath);
    void WriteAnalysisHeader(int projectCount, int packageCount, int vulnerabilityCount);
    void WriteAnalyzerResult(AnalyzerResult result);
    void WriteAnalysisSummary(AnalysisReport report);
}
