using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Backs up every file a fix pass is about to overwrite, so a <c>--fix</c> run is as undoable as a
/// migration. The backup directory is created lazily on the first write — a pass that changes
/// nothing must not leave an empty <c>.cpmigrate_backup</c> behind.
/// </summary>
internal sealed class FixBackupSession
{
    private readonly BackupSettings _settings;
    private readonly IBackupManager _backupManager;
    private readonly HashSet<string> _backedUp = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );
    private readonly List<BackupEntry> _entries = [];
    private readonly string _timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

    private string? _backupPath;

    private FixBackupSession(BackupSettings settings, IBackupManager backupManager)
    {
        _settings = settings;
        _backupManager = backupManager;
    }

    /// <summary>The props path the fix pass targeted, for the manifest's rollback bookkeeping.</summary>
    public required string PropsFilePath { get; init; }

    /// <summary>
    /// Whether the props file existed before the pass ran. Captured once — a fixer that creates it
    /// mid-pass must not flip the answer, or a later <c>--rollback</c> would leave the created file
    /// in place.
    /// </summary>
    public required bool PropsFileExisted { get; init; }

    public int FileCount => _entries.Count;

    public string? BackupPath => _backupPath;

    /// <summary>
    /// A session only exists when the pass can actually write and backups are enabled. Dry runs and
    /// <c>--no-backup</c> produce null, and the fix pass then runs exactly as it always has.
    /// </summary>
    /// <remarks>
    /// A default <c>--backup-dir</c> anchors at the props file's directory rather than the process
    /// cwd: <c>--analyze --fix -s /other/tree</c> should leave its backup inside the tree it edited,
    /// where <c>--rollback --backup-dir /other/tree</c> can find it — not wherever the shell stood.
    /// An explicit directory is honored as given.
    /// </remarks>
    public static FixBackupSession? TryCreate(FixRequest request, IBackupManager backupManager)
    {
        if (request.DryRun || request.Backup is not { Enabled: true } settings)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.BackupDir) || settings.BackupDir == ".")
        {
            var propsDir =
                Path.GetDirectoryName(Path.GetFullPath(request.PropsFilePath)) ?? ".";
            settings = settings with { BackupDir = propsDir };
        }

        return new FixBackupSession(settings, backupManager)
        {
            PropsFilePath = request.PropsFilePath,
            PropsFileExisted = File.Exists(request.PropsFilePath),
        };
    }

    /// <summary>
    /// Backs up <paramref name="path"/> before its first write in this pass. Files a fixer creates
    /// rather than overwrites have nothing to preserve — a created props file is still covered by
    /// the manifest's <c>PropsFileExisted</c>, which makes <c>--rollback</c> delete it again.
    /// </summary>
    public void BeforeWrite(string path)
    {
        if (!File.Exists(path) || !_backedUp.Add(Path.GetFullPath(path)))
        {
            return;
        }

        _backupPath ??= BackupManager.CreateBackupDirectory(_settings);

        var entry = _backupManager.CreateBackupForProject(
            _settings,
            path,
            _backupPath,
            _timestamp
        );
        if (entry is not null)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Writes the manifest that <c>--rollback</c> reads, then applies the same .gitignore handling a
    /// migration run would. Nothing to write means nothing happened — no manifest, no noise.
    /// </summary>
    public void WriteManifest()
    {
        if (_entries.Count == 0 || string.IsNullOrEmpty(_backupPath))
        {
            return;
        }

        var manifest = new BackupManifest
        {
            Timestamp = _timestamp,
            PropsFilePath = PropsFilePath,
            PropsFileExisted = PropsFileExisted,
            Backups = _entries,
        };

        // The fix pass is synchronous end to end; these two writes are tiny and local.
        BackupManager.WriteManifestAsync(_backupPath, manifest).GetAwaiter().GetResult();
        BackupManager.ManageGitIgnore(_settings, _backupPath).GetAwaiter().GetResult();
    }
}
