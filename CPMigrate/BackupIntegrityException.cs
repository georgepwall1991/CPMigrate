namespace CPMigrate;

/// <summary>
/// Thrown when a backup file's contents do not match the SHA-256 recorded for it at backup
/// time. Restoring such a backup would write corrupted or tampered bytes over the user's
/// project, so the copy is refused before it starts.
/// </summary>
public sealed class BackupIntegrityException : Exception
{
    public BackupIntegrityException(
        string originalPath,
        string backupFileName,
        string expectedSha256,
        string actualSha256
    )
        : base(
            $"Backup integrity check failed for '{originalPath}': backup file '{backupFileName}' does not match the SHA-256 recorded when it was created (expected {expectedSha256}, found {actualSha256}). The backup may be corrupted or tampered with; nothing was restored from it."
        )
    {
    }
}
