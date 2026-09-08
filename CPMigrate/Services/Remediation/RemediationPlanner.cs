using CPMigrate.Models;
using NuGet.Versioning;

namespace CPMigrate.Services.Remediation;

/// <summary>
/// Turns the vulnerabilities the .NET SDK reported into a set of version bumps.
///
/// The planner is pure decision-making: it reads findings, asks the oracle and the feed what it needs
/// to know, and produces a <see cref="RemediationPlan"/>. It writes nothing. That separation is what
/// lets <c>--remediate --dry-run</c> show precisely what a real run would do, and what makes every
/// interesting case here testable without a solution on disk.
/// </summary>
public sealed class RemediationPlanner
{
    private readonly IAdvisoryOracle _oracle;
    private readonly INuGetVersionLookupService _versionLookup;

    /// <summary>
    /// Creates a planner.
    /// </summary>
    /// <param name="oracle">Source of advisory version ranges.</param>
    /// <param name="versionLookup">Source of published package versions.</param>
    public RemediationPlanner(IAdvisoryOracle oracle, INuGetVersionLookupService versionLookup)
    {
        _oracle = oracle;
        _versionLookup = versionLookup;
    }

    /// <summary>
    /// Plans a remediation.
    /// </summary>
    /// <param name="vulnerabilities">Findings from the SDK's vulnerability scan.</param>
    /// <param name="allowMajor">Whether a fix that crosses a major version may be applied.</param>
    /// <param name="includePrerelease">Whether pre-release versions are acceptable targets.</param>
    /// <param name="onlyPackages">
    /// When set, restricts remediation to these package IDs. Findings outside the list are dropped
    /// from the plan entirely rather than reported as unfixed: the user narrowed the question.
    /// </param>
    /// <returns>The plan, one entry per vulnerable package.</returns>
    public async Task<RemediationPlan> PlanAsync(
        IReadOnlyList<VulnerabilityInfo> vulnerabilities,
        bool allowMajor,
        bool includePrerelease,
        IReadOnlyList<string>? onlyPackages = null
    )
    {
        ArgumentNullException.ThrowIfNull(vulnerabilities);

        var only = onlyPackages is { Count: > 0 }
            ? new HashSet<string>(onlyPackages, StringComparer.OrdinalIgnoreCase)
            : null;

        // Grouped by package because a version is pinned once, centrally, however many projects and
        // target frameworks reported it. A multi-target project yields one finding per framework, and
        // planning those separately would queue the same bump several times.
        var groups = vulnerabilities
            .Where(v => !string.IsNullOrWhiteSpace(v.PackageName))
            .Where(v => only == null || only.Contains(v.PackageName))
            .GroupBy(v => v.PackageName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var actions = new List<RemediationAction>();

        foreach (var group in groups)
        {
            actions.Add(await PlanPackageAsync(group.Key, [.. group], allowMajor, includePrerelease));
        }

        return new RemediationPlan(actions);
    }

    private async Task<RemediationAction> PlanPackageAsync(
        string packageName,
        IReadOnlyList<VulnerabilityInfo> findings,
        bool allowMajor,
        bool includePrerelease
    )
    {
        var severity = findings
            .OrderByDescending(f => SeverityRank(f.Severity))
            .Select(f => f.Severity)
            .First();

        var projects = findings
            .Select(f => string.IsNullOrEmpty(f.ProjectName) ? f.ProjectPath : f.ProjectName)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A package is treated as transitive only when every finding says so. One direct reference is
        // enough to make the existing pin the thing to move.
        var isTransitive = findings.All(f => f.IsTransitive);

        var advisoryIds = findings
            .Select(f => f.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedVersionText = findings
            .Select(f => f.ResolvedVersion)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

        if (!NuGetVersion.TryParse(resolvedVersionText, out var currentVersion))
        {
            return Unresolvable(
                packageName,
                resolvedVersionText,
                isTransitive,
                advisoryIds,
                severity,
                projects,
                RemediationOutcome.AdvisoryDataUnavailable,
                $"the resolved version '{resolvedVersionText}' could not be parsed"
            );
        }

        var advisories = new List<AdvisoryRecord>();
        var unreadable = new List<string>();

        foreach (var advisoryId in advisoryIds)
        {
            var record = await _oracle.LookupAsync(advisoryId, packageName);

            if (record == null)
            {
                unreadable.Add(advisoryId);
                continue;
            }

            advisories.Add(record);
        }

        if (unreadable.Count > 0)
        {
            // Partial advisory data is not a partial answer. A version that clears the advisories that
            // could be read may be squarely inside the range of one that could not, so a target
            // computed now would carry the authority of a proof it does not have.
            return Unresolvable(
                packageName,
                resolvedVersionText,
                isTransitive,
                advisoryIds,
                severity,
                projects,
                RemediationOutcome.AdvisoryDataUnavailable,
                $"no version data for {string.Join(", ", unreadable)}"
            );
        }

        var available = await _versionLookup.GetAllVersionsAsync(packageName);

        if (available == null || available.Count == 0)
        {
            return Unresolvable(
                packageName,
                resolvedVersionText,
                isTransitive,
                advisoryIds,
                severity,
                projects,
                RemediationOutcome.AdvisoryDataUnavailable,
                "the published version list could not be read"
            );
        }

        var cves = advisories
            .SelectMany(a => a.Aliases)
            .Where(a => a.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outcome = SafeVersionResolver.Resolve(currentVersion, advisories, available, includePrerelease);

        return outcome.Status switch
        {
            SafeVersionStatus.InMajor => new RemediationAction(
                packageName,
                currentVersion.ToNormalizedString(),
                outcome.Version!.ToNormalizedString(),
                isTransitive,
                IsMajorBump: false,
                RemediationOutcome.Planned,
                advisoryIds,
                cves,
                severity,
                projects,
                Reason: null
            ),

            SafeVersionStatus.CrossMajor => new RemediationAction(
                packageName,
                currentVersion.ToNormalizedString(),
                outcome.Version!.ToNormalizedString(),
                isTransitive,
                IsMajorBump: true,
                allowMajor ? RemediationOutcome.Planned : RemediationOutcome.WithheldMajor,
                advisoryIds,
                cves,
                severity,
                projects,
                allowMajor
                    ? null
                    : $"the fix is only in {outcome.Version.Major}.x; pass --allow-major to apply it"
            ),

            SafeVersionStatus.AdvisoryDoesNotCoverResolvedVersion => Unresolvable(
                packageName,
                currentVersion.ToNormalizedString(),
                isTransitive,
                advisoryIds,
                severity,
                projects,
                RemediationOutcome.AdvisoryDoesNotCoverResolvedVersion,
                $"the advisory data does not describe version {currentVersion.ToNormalizedString()}",
                cves
            ),

            _ => Unresolvable(
                packageName,
                currentVersion.ToNormalizedString(),
                isTransitive,
                advisoryIds,
                severity,
                projects,
                RemediationOutcome.NoFixAvailable,
                "no published version clears these advisories",
                cves
            ),
        };
    }

    private static RemediationAction Unresolvable(
        string packageName,
        string currentVersion,
        bool isTransitive,
        IReadOnlyList<string> advisoryIds,
        string severity,
        IReadOnlyList<string> projects,
        RemediationOutcome outcome,
        string reason,
        IReadOnlyList<string>? cves = null
    )
    {
        return new RemediationAction(
            packageName,
            currentVersion,
            TargetVersion: null,
            isTransitive,
            IsMajorBump: false,
            outcome,
            advisoryIds,
            cves ?? [],
            severity,
            projects,
            reason
        );
    }

    private static int SeverityRank(string severity)
    {
        return severity?.ToLowerInvariant() switch
        {
            "critical" => 4,
            "high" => 3,
            "moderate" => 2,
            "low" => 1,
            _ => 0,
        };
    }
}
