namespace CPMigrate.Services;

/// <summary>
/// Collects the unified diffs generated during a run and appends each one to the
/// <c>--diff-file</c> target. The file is created (or truncated) when the run begins and is
/// left in place even when no diffs are produced: an empty artifact means nothing changed,
/// while an absent one means the run crashed before it could say so.
/// </summary>
public sealed class DiffFileCollector
{
    private string? _targetPath;
    private bool _hasContent;

    /// <summary>True once <see cref="Begin"/> has run with a target path.</summary>
    public bool IsEnabled => _targetPath != null;

    /// <summary>
    /// Points the collector at <paramref name="path"/>, creating or truncating the file
    /// immediately so the artifact exists from the first moment of the run.
    /// </summary>
    public void Begin(string path)
    {
        _targetPath = path;
        _hasContent = false;
        File.WriteAllText(path, string.Empty);
    }

    /// <summary>
    /// Appends one unified diff. A no-op until <see cref="Begin"/> runs, so call sites can
    /// append unconditionally; consecutive diffs are separated by a blank line.
    /// </summary>
    public void Append(string? diff)
    {
        if (string.IsNullOrEmpty(diff) || _targetPath == null)
        {
            return;
        }

        // A byte-identical merge still yields a diff — headers with no hunks. Appending it
        // would break the contract that an empty artifact means "no changes", so only a diff
        // carrying at least one @@ hunk lands in the file.
        if (!diff.Split('\n').Any(line => line.StartsWith("@@ ", StringComparison.Ordinal)))
        {
            return;
        }

        File.AppendAllText(_targetPath, (_hasContent ? "\n" : string.Empty) + diff);
        _hasContent = true;
    }
}
