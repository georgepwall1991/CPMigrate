using System.Security.Cryptography;
using System.Text;

namespace CPMigrate.Models;

/// <summary>
/// Stable identity for an analyzer finding, used wherever a finding has to be recognised as "the
/// same one" across runs: SARIF <c>partialFingerprints</c> (so code scanning tracks a finding rather
/// than reopening it every build) and baseline suppression (so accepted debt stays accepted).
///
/// Both consumers must agree, so the computation lives here rather than in either of them.
/// </summary>
public static class AnalysisIssueIdentity
{
    /// <summary>
    /// Versioned so the scheme can change without silently invalidating stored fingerprints: a
    /// baseline written under an older scheme is recognisably older, and is rejected with a
    /// regenerate instruction rather than quietly matching nothing.
    ///
    /// <para>
    /// v2 identifies projects by their path relative to the scan root. v1 used file names, so two
    /// projects sharing a basename shared an identity and a baseline entry for one could suppress a
    /// finding in the other.
    /// </para>
    /// </summary>
    public const string Version = "v2";

    /// <summary>
    /// Computes the fingerprint for a finding.
    ///
    /// Deliberately excludes the description, because it carries the observed versions — a version
    /// inconsistency that drifts from "13.0.1, 12.0.3" to "13.0.2, 12.0.3" is still the same
    /// unresolved finding, and a fingerprint that changed with it would defeat both tracking and
    /// suppression. Package IDs are lowercased because NuGet treats them case-insensitively, and
    /// project identifiers are sorted because analyzers do not guarantee ordering.
    ///
    /// <para>
    /// Projects are identified by their path relative to the scan root, so two projects sharing a
    /// file name are distinct — and the value stays portable, which matters because a committed
    /// baseline has to match on every machine that runs the tool.
    /// </para>
    /// </summary>
    /// <param name="issue">The finding to identify.</param>
    /// <returns>A 32-character lowercase hex fingerprint.</returns>
    public static string Compute(AnalysisIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        var projects = issue
            .AffectedProjects.Select(name => name.ToLowerInvariant())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Unit separator: it cannot occur in a package ID or project name, so the parts stay
        // unambiguous — "A" + "B,C" cannot collide with "A,B" + "C".
        var seed = string.Join(
            '\u001F',
            issue.IssueCode.ToString(),
            issue.PackageName.ToLowerInvariant(),
            string.Join(',', projects)
        );

        // Appended only when there is one. Joining an empty discriminator would leave a trailing
        // separator, which changes the hash of *every* finding — committed baselines would stop
        // matching and accepted debt would start failing builds, without the scheme version
        // changing to say why. Caught by the test that pins an untouched fingerprint by value.
        var discriminator = Discriminator(issue);
        if (discriminator.Length > 0)
        {
            seed = string.Join('\u001F', seed, discriminator);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// An extra identifying part for rules where the package and the projects are not the whole
    /// finding.
    ///
    /// <para>
    /// <c>FloatingVersion</c> reports one finding per version specification, and a central pin names
    /// no project at all — so two different specifications for one package would otherwise share a
    /// fingerprint. <c>--write-baseline</c> would record one of them and the baseline would then
    /// suppress both, which is the quiet half of the accepted-debt failure: a finding nobody
    /// accepted, silently gone.
    /// </para>
    ///
    /// <para>
    /// Empty for every rule that does not publish a specification, so fingerprints computed before
    /// this existed are unchanged and committed baselines keep matching. That is why this is not a
    /// scheme bump: nothing that had an identity has a different one now.
    /// </para>
    /// </summary>
    private static string Discriminator(AnalysisIssue issue)
    {
        if (issue.Metadata is null)
        {
            return string.Empty;
        }

        var parts = DiscriminatingMetadata
            .Where(key => issue.Metadata.ContainsKey(key))
            .Select(key => issue.Metadata[key].Trim().ToLowerInvariant());

        return string.Join('\u001F', parts);
    }

    /// <summary>
    /// Metadata keys that identify a finding rather than describe it, in precedence order.
    ///
    /// <para>
    /// <c>versionSpecification</c> separates the several floating specifications one package can
    /// have. <c>propsFile</c> separates findings about different <c>Directory.Packages.props</c>
    /// files in one repository: those name the file as their package and no project at all, so
    /// without it a baseline accepting one would silently suppress the rest.
    /// </para>
    ///
    /// <para>
    /// A finding about the conventional root props file deliberately carries no <c>propsFile</c>
    /// key. It is the only file that could produce a finding before nested files were read, so
    /// adding one to the seed would change every stored fingerprint for it — a committed baseline
    /// would stop matching the High finding it had accepted, and SARIF would reopen it, on upgrade
    /// and with no scheme change to explain why. Emitting the key only for nested files leaves
    /// every pre-existing identity exactly as it was.
    /// </para>
    /// </summary>
    private static readonly string[] DiscriminatingMetadata = ["versionSpecification", "propsFile"];
}
