using System.Text.Json.Serialization;

namespace CPMigrate.Models;

/// <summary>
/// A recorded finding in a baseline. Carries the fingerprint the suppression is keyed on, plus
/// human-readable context so the file can be reviewed in a pull request rather than being an opaque
/// list of hashes — accepting technical debt is a decision someone should be able to see.
/// </summary>
/// <param name="Fingerprint">Stable identity from <see cref="AnalysisIssueIdentity"/>.</param>
/// <param name="IssueCode">Rule that produced the finding.</param>
/// <param name="Package">Package the finding concerns.</param>
/// <param name="Severity">Severity at the time the baseline was written.</param>
/// <param name="Projects">Projects the finding affected.</param>
public record BaselineFinding(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("issueCode")] string IssueCode,
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects
);

/// <summary>
/// A set of findings a team has accepted. Runs given <c>--baseline</c> exclude these from the
/// failure gate while still reporting them, so a repository with existing debt can adopt a CI gate
/// that fails only on <em>new</em> problems.
/// </summary>
public class BaselineFile
{
    /// <summary>Current baseline format version.</summary>
    public const string CurrentVersion = "1.0.0";

    /// <summary>Schema reference, for editor validation.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// Baseline format version. Read separately from the fingerprint scheme version so a format
    /// change and an identity change can be reasoned about independently.
    /// </summary>
    [JsonPropertyName("baselineVersion")]
    public string BaselineVersion { get; set; } = CurrentVersion;

    /// <summary>
    /// Fingerprint scheme the entries were computed with. A baseline written under a different
    /// scheme cannot be matched, and saying so beats silently suppressing nothing.
    /// </summary>
    [JsonPropertyName("fingerprintVersion")]
    public string FingerprintVersion { get; set; } = AnalysisIssueIdentity.Version;

    /// <summary>CPMigrate version that wrote the file, for provenance.</summary>
    [JsonPropertyName("createdWith")]
    public string? CreatedWith { get; set; }

    /// <summary>When the baseline was written.</summary>
    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    /// <summary>The accepted findings.</summary>
    [JsonPropertyName("findings")]
    public List<BaselineFinding> Findings { get; set; } = new();
}
