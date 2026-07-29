using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Renders an <see cref="AnalysisReport"/> as a SARIF 2.1.0 log so analyzer findings can be
/// uploaded to GitHub code scanning (or any other SARIF consumer) instead of being parsed
/// out of CPMigrate's bespoke JSON.
/// </summary>
/// <summary>
/// Whether a scan completed, and why not when it did not. SARIF distinguishes "the tool ran and
/// found nothing" from "the tool did not finish"; conflating the two turns an aborted scan into a
/// clean bill of health, which is the one failure mode a code-scanning gate cannot detect.
/// </summary>
/// <param name="Completed">True when the scan completed over every project it set out to scan.</param>
/// <param name="FailureMessage">Why the run is incomplete; null when it is not.</param>
public record SarifRunOutcome(bool Completed, string? FailureMessage = null)
{
    /// <summary>A scan that completed. Findings — or their absence — can be trusted.</summary>
    public static SarifRunOutcome Successful { get; } = new(true);

    /// <summary>A run that did not complete, carrying the reason for the SARIF notification.</summary>
    /// <param name="message">The reason the run is incomplete.</param>
    public static SarifRunOutcome Failed(string message)
    {
        return new SarifRunOutcome(false, message);
    }
}

public static class SarifFormatter
{
#pragma warning disable S1075 // URIs should not be hardcoded - the SARIF schema and repo home are fixed, published locations
    private const string SchemaUri =
        "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json";

    private const string InformationUri = "https://github.com/georgepwall1991/CPMigrate";
#pragma warning restore S1075

    /// <summary>Identifier for the URI base that all artifact locations are relative to.</summary>
    private const string UriBaseId = "SRCROOT";

    /// <summary>Fingerprint key; versioned so the scheme can change without colliding with old runs.</summary>
    private const string FingerprintKey = "cpmigrate/v1";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Formats an analysis report as a SARIF 2.1.0 log document.
    /// </summary>
    /// <param name="report">The analyzer findings to render.</param>
    /// <param name="packageInfo">Scanned package references, used to resolve project names to file paths.</param>
    /// <param name="basePath">The directory findings are reported relative to (the scan root).</param>
    /// <param name="outcome">
    /// Whether the scan actually completed. An incomplete scan producing zero findings is a false
    /// negative, not a clean result, so it must not be reported as a successful invocation.
    /// </param>
    /// <returns>An indented SARIF JSON document.</returns>
    public static string Format(
        AnalysisReport report,
        ProjectPackageInfo packageInfo,
        string basePath,
        SarifRunOutcome? outcome = null
    )
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(packageInfo);

        outcome ??= SarifRunOutcome.Successful;

        var rootDirectory = ResolveRootDirectory(basePath);
        var projectPaths = BuildProjectPathIndex(packageInfo);
        var lineLocator = new PackageDeclarationLocator();

        var issues = report
            .Results.SelectMany(analyzer =>
                analyzer.Issues.Select(issue => (Analyzer: analyzer, Issue: issue))
            )
            .ToList();

        var ruleIndexes = new Dictionary<AnalysisIssueCode, int>();
        var rules = new JsonArray();
        foreach (var code in issues.Select(entry => entry.Issue.IssueCode).Distinct())
        {
            ruleIndexes[code] = rules.Count;
            rules.Add(BuildRule(AnalysisRuleCatalog.Get(code)));
        }

        var results = new JsonArray();
        foreach (var (analyzer, issue) in issues)
        {
            results.Add(
                BuildResult(
                    analyzer,
                    issue,
                    ruleIndexes[issue.IssueCode],
                    rootDirectory,
                    projectPaths,
                    lineLocator
                )
            );
        }

        return BuildLog(rules, results, rootDirectory, outcome).ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// Renders a failed run as a SARIF log so that <c>--output Sarif</c> always produces a
    /// parseable document. The failure is reported the way SARIF expects — an unsuccessful
    /// invocation carrying a tool execution notification — rather than as a finding, so
    /// consumers do not mistake a crash for a code-quality issue.
    /// </summary>
    /// <param name="errorMessage">The failure to report.</param>
    /// <param name="basePath">The directory the run was rooted at.</param>
    /// <returns>An indented SARIF JSON document describing the failure.</returns>
    public static string FormatError(string errorMessage, string basePath)
    {
        return BuildLog(
                new JsonArray(),
                new JsonArray(),
                ResolveRootDirectory(basePath),
                SarifRunOutcome.Failed(errorMessage)
            )
            .ToJsonString(SerializerOptions);
    }

    private static JsonObject BuildLog(
        JsonArray rules,
        JsonArray results,
        string rootDirectory,
        SarifRunOutcome outcome
    )
    {
        var invocation = new JsonObject
        {
            ["executionSuccessful"] = outcome.Completed,
            ["endTimeUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        if (outcome.FailureMessage is not null)
        {
            invocation["toolExecutionNotifications"] = new JsonArray
            {
                new JsonObject
                {
                    ["level"] = "error",
                    ["message"] = new JsonObject { ["text"] = outcome.FailureMessage },
                },
            };
        }

        return new JsonObject
        {
            ["$schema"] = SchemaUri,
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject
                        {
                            ["name"] = "CPMigrate",
                            ["version"] = OutputMetadata.CurrentVersion,
                            ["semanticVersion"] = OutputMetadata.CurrentVersion,
                            ["informationUri"] = InformationUri,
                            ["rules"] = rules,
                        },
                    },
                    ["originalUriBaseIds"] = new JsonObject
                    {
                        [UriBaseId] = new JsonObject { ["uri"] = ToDirectoryUri(rootDirectory) },
                    },
                    ["columnKind"] = "utf16CodeUnits",
                    ["invocations"] = new JsonArray { invocation },
                    ["results"] = results,
                },
            },
        };
    }

    private static JsonObject BuildRule(AnalysisRule rule)
    {
        return new JsonObject
        {
            ["id"] = rule.Id,
            ["name"] = rule.Id,
            ["shortDescription"] = new JsonObject { ["text"] = rule.ShortDescription },
            ["fullDescription"] = new JsonObject { ["text"] = rule.FullDescription },
            ["helpUri"] = rule.HelpUri,
            ["help"] = new JsonObject { ["text"] = rule.FullDescription },
            ["properties"] = new JsonObject
            {
                ["tags"] = new JsonArray(
                    rule.Tags.Select(tag => (JsonNode)JsonValue.Create(tag)!).ToArray()
                ),
            },
        };
    }

    private static JsonObject BuildResult(
        AnalyzerResult analyzer,
        AnalysisIssue issue,
        int ruleIndex,
        string rootDirectory,
        IReadOnlyDictionary<string, List<string>> projectPaths,
        PackageDeclarationLocator lineLocator
    )
    {
        var properties = new JsonObject
        {
            ["package"] = issue.PackageName,
            ["severity"] = issue.Severity.ToString(),
            ["fixable"] = issue.Fixable,
            ["analyzer"] = analyzer.AnalyzerName,
        };

        foreach (var (key, value) in issue.Metadata ?? new Dictionary<string, string>())
        {
            // Analyzer metadata keys are free-form; never let one shadow a field we own.
            if (!properties.ContainsKey(key))
            {
                properties[key] = value;
            }
        }

        return new JsonObject
        {
            ["ruleId"] = AnalysisRuleCatalog.Get(issue.IssueCode).Id,
            ["ruleIndex"] = ruleIndex,
            ["level"] = MapLevel(issue.Severity),
            ["message"] = new JsonObject { ["text"] = BuildMessage(issue) },
            ["locations"] = BuildLocations(issue, rootDirectory, projectPaths, lineLocator),
            ["partialFingerprints"] = new JsonObject
            {
                [FingerprintKey] = ComputeFingerprint(issue),
            },
            ["properties"] = properties,
        };
    }

    private static string BuildMessage(AnalysisIssue issue)
    {
        return $"{issue.PackageName}: {issue.Description}";
    }

    /// <summary>
    /// Maps CPMigrate severities onto the three levels SARIF consumers understand.
    /// High and Critical become errors so a code-scanning gate can fail on them.
    /// </summary>
    private static string MapLevel(AnalysisSeverity severity)
    {
        return severity switch
        {
            AnalysisSeverity.Critical or AnalysisSeverity.High => "error",
            AnalysisSeverity.Moderate => "warning",
            _ => "note",
        };
    }

    private static JsonArray BuildLocations(
        AnalysisIssue issue,
        string rootDirectory,
        IReadOnlyDictionary<string, List<string>> projectPaths,
        PackageDeclarationLocator lineLocator
    )
    {
        var locations = new JsonArray();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectName in issue.AffectedProjects)
        {
            if (!projectPaths.TryGetValue(projectName, out var paths))
            {
                // The analyzer named a project we never scanned (or reported a label rather than a
                // file). SARIF allows a result with no locations, which is better than inventing one.
                continue;
            }

            foreach (var path in paths)
            {
                var relative = ToRelativeUri(rootDirectory, path);
                if (!emitted.Add(relative))
                {
                    continue;
                }

                var physicalLocation = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject
                    {
                        ["uri"] = relative,
                        ["uriBaseId"] = UriBaseId,
                    },
                };

                var line = lineLocator.FindPackageLine(path, issue.PackageName);
                if (line.HasValue)
                {
                    physicalLocation["region"] = new JsonObject { ["startLine"] = line.Value };
                }

                locations.Add(new JsonObject { ["physicalLocation"] = physicalLocation });
            }
        }

        return locations;
    }

    /// <summary>
    /// Builds a stable identity for a finding so code scanning can track it across runs.
    /// Project names are sorted because analyzers do not guarantee ordering.
    /// </summary>
    private static string ComputeFingerprint(AnalysisIssue issue)
    {
        var projects = issue
            .AffectedProjects.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Unit separator: it cannot occur in a package ID or project name, so the parts stay unambiguous.
        var seed = string.Join(
            '\u001F',
            issue.IssueCode.ToString(),
            issue.PackageName.ToLowerInvariant(),
            string.Join(',', projects)
        );

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Indexes scanned projects by file name so analyzer findings — which carry names, not
    /// paths — can be resolved back to files. A name may map to several paths in a solution
    /// that reuses project names across directories.
    /// </summary>
    private static Dictionary<string, List<string>> BuildProjectPathIndex(
        ProjectPackageInfo packageInfo
    )
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in packageInfo.References)
        {
            if (
                string.IsNullOrWhiteSpace(reference.ProjectName)
                || string.IsNullOrWhiteSpace(reference.ProjectPath)
            )
            {
                continue;
            }

            if (!index.TryGetValue(reference.ProjectName, out var paths))
            {
                paths = new List<string>();
                index[reference.ProjectName] = paths;
            }

            if (!paths.Contains(reference.ProjectPath, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(reference.ProjectPath);
            }
        }

        return index;
    }

    private static string ResolveRootDirectory(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return Directory.GetCurrentDirectory();
        }

        var full = Path.GetFullPath(basePath);
        return File.Exists(full) ? Path.GetDirectoryName(full) ?? full : full;
    }

    private static string ToDirectoryUri(string directory)
    {
        var withSeparator = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        return new Uri(withSeparator).AbsoluteUri;
    }

    /// <summary>
    /// Produces a forward-slash relative URI. Paths outside the scan root keep their absolute
    /// form rather than being expressed with '..' segments, which SARIF consumers reject.
    /// </summary>
    private static string ToRelativeUri(string rootDirectory, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(rootDirectory, fullPath);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return new Uri(fullPath).AbsoluteUri;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
    }
}

/// <summary>
/// Best-effort lookup of the line where a project declares a package, so SARIF results can
/// annotate the exact <c>PackageReference</c> instead of the whole file. Results are cached
/// per file because one project usually contributes several findings.
/// </summary>
internal sealed class PackageDeclarationLocator
{
    private readonly Dictionary<string, string[]?> _fileCache = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Returns the 1-based line declaring <paramref name="packageName"/>, or null when the file
    /// is unreadable or the package is not declared inline (for example, it is transitive or the
    /// version already lives in Directory.Packages.props).
    /// </summary>
    public int? FindPackageLine(string projectPath, string packageName)
    {
        var lines = ReadLines(projectPath);
        if (lines is null || string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var needle = $"\"{packageName}\"";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (
                line.Contains("Include=", StringComparison.OrdinalIgnoreCase)
                && line.Contains(needle, StringComparison.OrdinalIgnoreCase)
            )
            {
                return i + 1;
            }
        }

        return null;
    }

    private string[]? ReadLines(string projectPath)
    {
        if (_fileCache.TryGetValue(projectPath, out var cached))
        {
            return cached;
        }

        string[]? lines = null;
        try
        {
            if (File.Exists(projectPath))
            {
                lines = File.ReadAllLines(projectPath);
            }
        }
        catch (IOException)
        {
            lines = null;
        }
        catch (UnauthorizedAccessException)
        {
            lines = null;
        }

        _fileCache[projectPath] = lines;
        return lines;
    }
}
