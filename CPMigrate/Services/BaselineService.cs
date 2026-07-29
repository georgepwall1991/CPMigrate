using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Outcome of matching a report against a baseline.
/// </summary>
/// <param name="Report">The report with baselined findings marked as suppressed.</param>
/// <param name="Suppressed">How many findings the baseline accounted for.</param>
/// <param name="Stale">
/// Baseline entries that matched nothing. These are findings that have since been fixed, so the
/// entry is dead weight — surfacing them is what stops a baseline growing forever.
/// </param>
public record BaselineMatch(
    AnalysisReport Report,
    int Suppressed,
    IReadOnlyList<BaselineFinding> Stale
);

/// <summary>
/// Reads and writes baseline files, and applies one to an analysis report.
///
/// The point of a baseline is adoption: a repository with existing debt cannot turn on a CI gate
/// that fails on everything, so it records what is already there and gates only on what is new.
/// Suppressed findings are still reported — the debt stays visible, it just stops blocking.
/// </summary>
public sealed class BaselineService
{
#pragma warning disable S1075 // URIs should not be hardcoded - the published schema lives at a fixed location
    private const string SchemaUri =
        "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/schemas/cpmigrate-baseline.schema.json";
#pragma warning restore S1075

    /// <summary>Default file name when <c>--baseline</c> does not name one.</summary>
    public const string DefaultFileName = ".cpmigrate-baseline.json";

    private readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds a baseline from a report. Findings are ordered deterministically so regenerating an
    /// unchanged baseline produces no diff.
    /// </summary>
    /// <param name="report">The findings to accept.</param>
    public BaselineFile Create(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var findings = report
            .Results.SelectMany(result => result.Issues)
            .Select(issue => new BaselineFinding(
                AnalysisIssueIdentity.Compute(issue),
                issue.IssueCode.ToString(),
                issue.PackageName,
                issue.Severity.ToString(),
                issue.AffectedProjects.OrderBy(name => name, StringComparer.Ordinal).ToList()
            ))
            .GroupBy(finding => finding.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToList();

        return new BaselineFile
        {
            Schema = SchemaUri,
            CreatedWith = OutputMetadata.CurrentVersion,
            CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Findings = findings,
        };
    }

    /// <summary>
    /// Serializes a baseline to disk.
    /// </summary>
    /// <param name="baseline">The baseline to write.</param>
    /// <param name="path">Destination file.</param>
    public async Task WriteAsync(BaselineFile baseline, string path)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(baseline, _writeOptions));
    }

    /// <summary>
    /// Reads a baseline from disk.
    /// </summary>
    /// <param name="path">The baseline file.</param>
    /// <returns>The baseline, or an error message describing why it could not be read.</returns>
    public (BaselineFile? Baseline, string? ErrorMessage) Read(string path)
    {
        if (!File.Exists(path))
        {
            return (null, $"Baseline file not found: {path}");
        }

        BaselineFile? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<BaselineFile>(
                File.ReadAllText(path),
                _readOptions
            );
        }
        catch (JsonException ex)
        {
            return (null, $"Failed to parse baseline file {path}: {ex.Message}");
        }
        catch (IOException ex)
        {
            return (null, $"Failed to read baseline file {path}: {ex.Message}");
        }

        if (baseline is null)
        {
            return (null, $"Baseline file {path} is empty.");
        }

        if (
            !string.Equals(baseline.BaselineVersion, BaselineFile.CurrentVersion, StringComparison.Ordinal)
        )
        {
            return (
                null,
                $"Baseline file {path} declares format version '{baseline.BaselineVersion}', "
                    + $"but this version of CPMigrate reads '{BaselineFile.CurrentVersion}'."
            );
        }

        if (baseline.Findings is null)
        {
            // Property initializers make every field look present, so a null or missing array
            // survives deserialization and would fault later, inside Apply.
            return (
                null,
                $"Baseline file {path} has no 'findings' array. Regenerate it with --write-baseline."
            );
        }

        var invalid = baseline.Findings.FirstOrDefault(finding =>
            string.IsNullOrWhiteSpace(finding.Fingerprint)
            || string.IsNullOrWhiteSpace(finding.IssueCode)
            || finding.Projects is null
        );
        if (invalid is not null)
        {
            return (
                null,
                $"Baseline file {path} contains an entry missing a fingerprint, issue code, or "
                    + "project list. Regenerate it with --write-baseline."
            );
        }

        if (
            !string.Equals(
                baseline.FingerprintVersion,
                AnalysisIssueIdentity.Version,
                StringComparison.Ordinal
            )
        )
        {
            // Matching would silently suppress nothing, which looks identical to "no debt accepted".
            return (
                null,
                $"Baseline file {path} uses fingerprint scheme '{baseline.FingerprintVersion}', "
                    + $"but this version of CPMigrate computes '{AnalysisIssueIdentity.Version}'. "
                    + "Regenerate it with --write-baseline."
            );
        }

        return (baseline, null);
    }

    /// <summary>
    /// Marks every finding present in the baseline as suppressed, and reports which baseline entries
    /// no longer match anything.
    /// </summary>
    /// <param name="report">The report to annotate.</param>
    /// <param name="baseline">The accepted findings.</param>
    public BaselineMatch Apply(AnalysisReport report, BaselineFile baseline)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(baseline);

        var accepted = baseline
            .Findings.Select(finding => finding.Fingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suppressed = 0;

        var results = new List<AnalyzerResult>(report.Results.Count);
        foreach (var analyzerResult in report.Results)
        {
            var issues = new List<AnalysisIssue>(analyzerResult.Issues.Count);
            foreach (var issue in analyzerResult.Issues)
            {
                var fingerprint = AnalysisIssueIdentity.Compute(issue);
                if (accepted.Contains(fingerprint))
                {
                    matched.Add(fingerprint);
                    suppressed++;
                    issues.Add(issue with { Suppressed = true });
                    continue;
                }

                issues.Add(issue);
            }

            results.Add(analyzerResult with { Issues = issues });
        }

        var stale = baseline
            .Findings.Where(finding => !matched.Contains(finding.Fingerprint))
            .ToList();

        return new BaselineMatch(report with { Results = results }, suppressed, stale);
    }
}
