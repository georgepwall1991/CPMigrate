namespace CPMigrate.Models;

/// <summary>
/// The outcome of a <c>--update</c> run, as data. <c>PerformUpdateAsync</c> used to answer with a
/// <see langword="bool"/>, which told the router how to exit but not why — "user said no" and
/// "the feed never answered" were the same <see langword="false"/>. Under <c>--output Json</c>
/// the distinction is the payload: a CI consumer gating on <c>status</c> needs
/// <c>checkFailed</c> (re-run later) to read differently from <c>declined</c> (a human chose).
/// </summary>
/// <param name="Status">
/// The outcome as a single token: <c>updated</c> (a newer version installed), <c>alreadyLatest</c>
/// (the feed answered and nothing newer exists), <c>checkFailed</c> (the version list could not be
/// fetched — re-run later), <c>dryRun</c> (a newer version exists and <c>--dry-run</c> was passed —
/// reported, not installed), <c>nonInteractive</c> (a newer version exists but no TTY and no
/// <c>--force</c> — the console path prints the unattended command instead), <c>declined</c>
/// (the prompt ran and the user said no), or <c>failed</c> (<c>dotnet tool update</c> ran and
/// lost — <paramref name="Error"/> carries what it said).
/// </param>
/// <param name="CurrentVersion">The version of the running binary.</param>
/// <param name="LatestVersion">
/// The newest stable version the feed reported, or <see langword="null"/> when the check itself
/// failed — absent rather than invented, since a consumer cannot act on a version nobody saw.
/// </param>
/// <param name="Error">
/// What the failed update attempt reported (<c>dotnet tool update</c>'s stderr, or the exception
/// message when it could not be started). <see langword="null"/> on every other status.
/// </param>
public sealed record SelfUpdateResult(
    string Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? Error = null
)
{
    /// <summary>
    /// The statuses that mean the command did what was asked — mirrored by the exit code.
    /// <c>dryRun</c> counts: the ask was a check, and the check answered.
    /// </summary>
    public bool Success => Status is "updated" or "alreadyLatest" or "dryRun";
}
