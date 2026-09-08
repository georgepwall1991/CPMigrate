namespace CPMigrate.Services.Remediation;

/// <summary>
/// What remediation intends to do about one package, or why it cannot.
/// </summary>
public enum RemediationOutcome
{
    /// <summary>A fix version was found and is safe to apply.</summary>
    Planned,

    /// <summary>
    /// A fix exists but only in a higher major version, and <c>--allow-major</c> was not passed. The
    /// bump is reported in full so the decision can be made deliberately.
    /// </summary>
    WithheldMajor,

    /// <summary>No published version clears the advisories.</summary>
    NoFixAvailable,

    /// <summary>
    /// The advisory database could not be reached. Distinct from <see cref="NoFixAvailable"/>, which
    /// is a real answer; this is the absence of one, and the correct response is to re-run.
    /// </summary>
    AdvisoryDataUnavailable,

    /// <summary>
    /// The database answered and does not carry this advisory, so no fix version can be computed
    /// from it.
    ///
    /// Deliberately not <see cref="AdvisoryDataUnavailable"/>: that one means "ask again", and a
    /// definitive 404 will say the same thing forever. Conflating them makes CI retry a permanent
    /// answer and — because unreadable data aborts the whole run — blocks every other package's fix
    /// behind one advisory nobody can look up.
    /// </summary>
    AdvisoryNotInDatabase,

    /// <summary>The advisory data does not describe the resolved version, so no fix can be proven.</summary>
    AdvisoryDoesNotCoverResolvedVersion,
}

/// <summary>
/// One package's remediation, decided but not yet applied.
/// </summary>
/// <param name="PackageName">The vulnerable package.</param>
/// <param name="CurrentVersion">The version the graph resolves to today.</param>
/// <param name="TargetVersion">The version to move to, when one was found.</param>
/// <param name="IsTransitive">
/// Whether the package is only reached through the graph. A transitive-only remediation adds a new
/// central pin rather than moving an existing one.
/// </param>
/// <param name="IsMajorBump">Whether the target crosses a major version boundary.</param>
/// <param name="Outcome">What will happen, or why nothing will.</param>
/// <param name="AdvisoryIds">The advisories the SDK reported against this package.</param>
/// <param name="Cves">CVE identifiers for those advisories, when the database publishes them.</param>
/// <param name="Severity">The worst severity the SDK reported for this package.</param>
/// <param name="AffectedProjects">Projects the advisories were reported in.</param>
/// <param name="Reason">A human-readable explanation, present whenever the outcome is not <see cref="RemediationOutcome.Planned"/>.</param>
public sealed record RemediationAction(
    string PackageName,
    string CurrentVersion,
    string? TargetVersion,
    bool IsTransitive,
    bool IsMajorBump,
    RemediationOutcome Outcome,
    IReadOnlyList<string> AdvisoryIds,
    IReadOnlyList<string> Cves,
    string Severity,
    IReadOnlyList<string> AffectedProjects,
    string? Reason
);

/// <summary>
/// Everything remediation decided, before anything is written.
///
/// A plan is produced identically whether or not it will be applied, which is what makes
/// <c>--remediate --dry-run</c> an honest preview rather than a separate code path with its own
/// behaviour.
/// </summary>
/// <param name="Actions">One entry per vulnerable package.</param>
public sealed record RemediationPlan(IReadOnlyList<RemediationAction> Actions)
{
    /// <summary>The actions that will actually be written and verified.</summary>
    /// <returns>Every action whose outcome is <see cref="RemediationOutcome.Planned"/>.</returns>
    public IReadOnlyList<RemediationAction> GetApplicable() =>
        Actions.Where(a => a.Outcome == RemediationOutcome.Planned).ToList();

    /// <summary>
    /// Whether any advisory could not be looked up. This is the condition that makes a run
    /// incomplete rather than merely unsuccessful: a fix computed while part of the advisory data was
    /// unreadable is not a fix anyone should gate a release on.
    /// </summary>
    public bool HasUnavailableAdvisoryData =>
        Actions.Any(a => a.Outcome == RemediationOutcome.AdvisoryDataUnavailable);

    /// <summary>Actions reported but deliberately not applied.</summary>
    /// <returns>Every action whose outcome is not <see cref="RemediationOutcome.Planned"/>.</returns>
    public IReadOnlyList<RemediationAction> GetNotApplied() =>
        Actions.Where(a => a.Outcome != RemediationOutcome.Planned).ToList();

    /// <summary>Whether the plan would change anything.</summary>
    public bool IsEmpty => Actions.Count == 0;
}
