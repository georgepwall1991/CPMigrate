using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using CPMigrate.Analyzers;
using CPMigrate.Models;

namespace CPMigrate.Services;

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

/// <summary>
/// Renders an <see cref="AnalysisReport"/> as a SARIF 2.1.0 log so analyzer findings can be
/// uploaded to GitHub code scanning (or any other SARIF consumer) instead of being parsed
/// out of CPMigrate's bespoke JSON.
/// </summary>
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

        var projectPaths = BuildProjectPathIndex(packageInfo);
        var rootDirectory = WidenRootToCoverProjects(
            ResolveRootDirectory(basePath),
            projectPaths.AllPaths
        );
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
        ProjectPathIndex projectPaths,
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
        ProjectPathIndex projectPaths,
        PackageDeclarationLocator lineLocator
    )
    {
        var locations = new JsonArray();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectName in issue.AffectedProjects)
        {
            var paths = projectPaths.Resolve(projectName, issue.PackageName);
            if (paths.Count == 0)
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

    private static ProjectPathIndex BuildProjectPathIndex(ProjectPackageInfo packageInfo)
    {
        return ProjectPathIndex.Build(packageInfo);
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

    /// <summary>
    /// Widens the URI base until it contains every reported project. A solution under <c>build/</c>
    /// that references <c>../src/App.csproj</c> would otherwise leave that project outside the
    /// scan root, forcing an absolute <c>file://</c> URI that code scanning cannot map back to a
    /// checked-out file — silently dropping the annotation the feature exists to produce.
    /// </summary>
    private static string WidenRootToCoverProjects(
        string rootDirectory,
        IEnumerable<string> projectPaths
    )
    {
        var root = rootDirectory;

        foreach (var projectPath in projectPaths)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            root = FindCommonAncestor(root, directory) ?? root;
        }

        return root;
    }

    /// <summary>
    /// Returns the deepest directory containing both paths, or null when they share no root
    /// (separate Windows drives, for example), where no relative URI is possible.
    /// </summary>
    private static string? FindCommonAncestor(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var firstSegments = SplitPath(first);
        var secondSegments = SplitPath(second);

        var shared = 0;
        while (
            shared < firstSegments.Length
            && shared < secondSegments.Length
            && string.Equals(firstSegments[shared], secondSegments[shared], comparison)
        )
        {
            shared++;
        }

        if (shared == 0)
        {
            return null;
        }

        if (shared == firstSegments.Length)
        {
            return first;
        }

        var ancestor = string.Join(Path.DirectorySeparatorChar, firstSegments.Take(shared));

        // On Unix the leading separator is the first (empty) segment, which join drops.
        return Path.IsPathRooted(first) && !Path.IsPathRooted(ancestor)
            ? Path.DirectorySeparatorChar + ancestor
            : ancestor;
    }

    private static string[] SplitPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
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

        // artifactLocation.uri is a URI reference, not a filesystem path: a raw space, '#', or '%'
        // makes it invalid, and a consumer either rejects the location or resolves it to the wrong
        // file. Encode per segment so the separators survive.
        var segments = relative
            .Replace('\\', '/')
            .Replace(Path.DirectorySeparatorChar, '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        return string.Join('/', segments);
    }
}

/// <summary>
/// Locates the line where a project declares a package, so SARIF results can annotate the exact
/// declaration instead of the whole file.
///
/// This parses the project as XML rather than scanning text. A project file is XML, so quoting
/// style (<c>Include='X'</c> vs <c>Include="X"</c>), the <c>Update=</c> form, child-element
/// syntax, and commented-out declarations all have to be understood the way MSBuild understands
/// them — a text search gets each of those wrong, and a wrong line means the annotation lands on
/// code that has nothing to do with the finding.
///
/// Parsed documents are cached because one project usually contributes several findings.
/// </summary>
internal sealed class PackageDeclarationLocator
{
    private readonly Dictionary<string, XDocument?> _documentCache = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Returns the 1-based line declaring <paramref name="packageName"/>, or null when the project
    /// is unreadable or unparseable, or the package is not declared in this project (for example,
    /// it arrives transitively).
    /// </summary>
    /// <param name="projectPath">Full path to the project file.</param>
    /// <param name="packageName">The package ID to locate.</param>
    public int? FindPackageLine(string projectPath, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var document = LoadDocument(projectPath);
        if (document?.Root is null)
        {
            return null;
        }

        foreach (var element in document.Root.Descendants())
        {
            if (!IsPackageItem(element.Name.LocalName))
            {
                continue;
            }

            var declared =
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (!string.Equals(declared, packageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
            {
                return lineInfo.LineNumber;
            }
        }

        return null;
    }

    /// <summary>
    /// Matches the item types that carry a package ID. <c>PackageVersion</c> and
    /// <c>GlobalPackageReference</c> are included so a finding against a centrally managed package
    /// points at its Directory.Packages.props entry rather than falling back to the file.
    /// </summary>
    private static bool IsPackageItem(string elementName)
    {
        return elementName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase)
            || elementName.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase)
            || elementName.Equals("GlobalPackageReference", StringComparison.OrdinalIgnoreCase);
    }

    private XDocument? LoadDocument(string projectPath)
    {
        if (_documentCache.TryGetValue(projectPath, out var cached))
        {
            return cached;
        }

        XDocument? document = null;
        try
        {
            if (File.Exists(projectPath))
            {
                document = XDocument.Load(projectPath, LoadOptions.SetLineInfo);
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // A project that will not parse still deserves a file-level annotation, so fall back
            // rather than failing the whole report.
            document = null;
        }

        _documentCache[projectPath] = document;
        return document;
    }
}

/// <summary>
/// Resolves the project names carried by analyzer findings back to project files.
///
/// Findings name projects rather than pointing at them, and a solution may reuse a project name
/// across directories (<c>src/App/App.csproj</c> and <c>tests/App/App.csproj</c>). Reporting every
/// candidate would annotate files that never declared the package, so the index also records which
/// project declared which package and prefers that narrower answer.
/// </summary>
internal sealed class ProjectPathIndex
{
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    private readonly Dictionary<string, List<string>> _byName = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<(string Project, string Package), List<string>> _byNameAndPackage =
        new();

    private ProjectPathIndex() { }

    /// <summary>Every distinct project path the scan covered.</summary>
    public IEnumerable<string> AllPaths =>
        _byName.Values.SelectMany(paths => paths).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the index from the scanned package references.
    /// </summary>
    /// <param name="packageInfo">The references the analysis was built from.</param>
    public static ProjectPathIndex Build(ProjectPackageInfo packageInfo)
    {
        var index = new ProjectPathIndex();

        foreach (var reference in packageInfo.References)
        {
            if (
                string.IsNullOrWhiteSpace(reference.ProjectName)
                || string.IsNullOrWhiteSpace(reference.ProjectPath)
            )
            {
                continue;
            }

            Add(index._byName, reference.ProjectName, reference.ProjectPath);
            Add(
                index._byNameAndPackage,
                (reference.ProjectName, reference.PackageName),
                reference.ProjectPath
            );
        }

        return index;
    }

    /// <summary>
    /// Returns the project files a finding should be reported against: the ones declaring the
    /// package when that is known, otherwise every project with the given name. Findings that are
    /// not about a single package (framework alignment, for example) fall into the second case,
    /// where reporting every candidate beats reporting none.
    /// </summary>
    /// <param name="projectName">The project name the analyzer reported.</param>
    /// <param name="packageName">The package the finding concerns, if any.</param>
    public IReadOnlyList<string> Resolve(string projectName, string packageName)
    {
        if (
            !string.IsNullOrWhiteSpace(packageName)
            && _byNameAndPackage.TryGetValue((projectName, packageName), out var exact)
        )
        {
            return exact;
        }

        return _byName.TryGetValue(projectName, out var candidates) ? candidates : None;
    }

    private static void Add<TKey>(Dictionary<TKey, List<string>> index, TKey key, string path)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var paths))
        {
            paths = new List<string>();
            index[key] = paths;
        }

        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }
}
