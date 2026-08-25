using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Fixes transitive conflicts by pinning the best version in Directory.Packages.props.
/// </summary>
public class TransitiveConflictFixer : IFixer
{
    private readonly VersionResolver _versionResolver;

    public TransitiveConflictFixer(VersionResolver versionResolver)
    {
        _versionResolver = versionResolver;
    }

    public string Name => "Transitive Conflict Pinning";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.TransitiveConflict;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        var propsPath = request.PropsFilePath;
        if (!File.Exists(propsPath))
        {
            return FixResult.Failed("Directory.Packages.props not found. Transitive pinning requires an existing CPM setup.");
        }

        // Find best version from packageInfo
        var versions = packageInfo.References
            .Where(r => r.PackageName.Equals(issue.PackageName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Version)
            .ToHashSet();

        if (versions.Count == 0)
        {
            return FixResult.Failed("Could not determine versions for package.");
        }

        var bestVersion = _versionResolver.ResolveVersion(versions, request.ConflictStrategy);

        var originalContent = File.ReadAllText(propsPath);
        var hasActivePackagePin = false;
        XDocument? propsDocument = null;

        try
        {
            propsDocument = XDocument.Parse(originalContent, LoadOptions.PreserveWhitespace);
            var targetPins = propsDocument
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase))
                .Where(element => element.Attributes().Any(attribute =>
                    (attribute.Name.LocalName.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                     attribute.Name.LocalName.Equals("Update", StringComparison.OrdinalIgnoreCase)) &&
                    attribute.Value.Equals(issue.PackageName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            hasActivePackagePin = targetPins.Count > 0;

            if (targetPins.Any(pin => IsConditional(pin) || pin.Descendants().Any(IsConditional)))
            {
                return FixResult.Failed(
                    $"Cannot pin {issue.PackageName} because its central package version is conditional.");
            }
        }
        catch (XmlException)
        {
            return FixResult.Failed(
                $"Could not safely inspect Directory.Packages.props before pinning {issue.PackageName}.");
        }

        string updatedContent;

        // Check if package already exists in props
        if (hasActivePackagePin)
        {
            // Update existing version
            var pattern = $@"(<PackageVersion\s+(?:Include|Update)=""{Regex.Escape(issue.PackageName)}""\s+Version="")([^""]+)("")";
            updatedContent = Regex.Replace(originalContent, pattern,
                match => match.Groups[1].Value + bestVersion + match.Groups[3].Value,
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

            // MSBuild also accepts Version metadata as a child element. Keep the surrounding XML
            // layout intact while changing only the value, so a repository using that form is not
            // left with the original conflicting pin.
            var childPattern = $@"(<PackageVersion\b[^>]*(?:Include|Update)=""{Regex.Escape(issue.PackageName)}""[^>]*(?<!/)>(?:(?!</?PackageVersion\b)[\s\S])*?<Version>\s*)([^<]*?)(\s*</Version>)";
            updatedContent = Regex.Replace(
                updatedContent,
                childPattern,
                match => match.Groups[1].Value + bestVersion + match.Groups[3].Value,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2)
            );
        }
        else
        {
            // Add one new entry, into the first unconditional top-level ItemGroup — or a fresh one.
            updatedContent = InsertCentralPin(propsDocument, issue.PackageName, bestVersion, originalContent);
        }

        if (updatedContent == originalContent)
        {
            return FixResult.NoFixNeeded("Version already aligned or package entry not found in suitable format.");
        }

        if (!request.DryRun)
        {
            File.WriteAllText(propsPath, updatedContent);
        }

        return FixResult.Succeeded(
            $"Pinned {issue.PackageName} to version {bestVersion} in Directory.Packages.props",
            new List<FileChange> { new FileChange(propsPath, "Modified", "...", $"Pinned {issue.PackageName} to {bestVersion}") }
        );
    }

    /// <summary>
    /// Adds a single <c>PackageVersion</c> entry for the package, preserving the surrounding layout.
    ///
    /// A plain <see cref="string.Replace(string,string)"/> over <c>&lt;/ItemGroup&gt;</c> cloned the pin
    /// into every group in the file, including groups reserved for one framework by a Condition or nested
    /// in a Choose or Target — where a central pin either does not apply everywhere it must or is not part
    /// of restore evaluation at all. The pin therefore goes into the first unconditional top-level
    /// ItemGroup; a file with none gets a fresh one before the project closes.
    /// </summary>
    private static string InsertCentralPin(
        XDocument document,
        string packageName,
        string bestVersion,
        string originalContent
    )
    {
        var root = document.Root;
        if (root is null)
        {
            return originalContent;
        }

        // XmlWriter rewrites every newline to the host's Environment.NewLine, so the serialized form
        // is normalized back to the endings the file actually uses before it is written.
        var fileUsesCrlf = originalContent.Contains("\r\n", StringComparison.Ordinal);
        var newline = fileUsesCrlf ? "\r\n" : "\n";
        var pin = new XElement(
            "PackageVersion",
            new XAttribute("Include", packageName),
            new XAttribute("Version", bestVersion)
        );

        var targetGroup = root
            .Descendants("ItemGroup")
            .Where(group => !group.Ancestors().Any(ancestor =>
                ancestor.Name.LocalName is "Target" or "Choose" or "When" or "Otherwise"
            ))
            .FirstOrDefault(group => !IsConditional(group));

        if (targetGroup is null)
        {
            var itemGroup = new XElement(
                "ItemGroup",
                new XText(newline + "  "),
                pin,
                new XText(newline + "  ")
            );
            if (root.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value))
            {
                trailing.AddBeforeSelf(new XText(newline + "  "), itemGroup);
            }
            else
            {
                root.Add(itemGroup);
            }

            return Serialize(document, fileUsesCrlf);
        }

        var groupIndent = IndentOf(targetGroup.PreviousNode as XText);
        var childIndent = $"{groupIndent}  ";

        if (targetGroup.Elements().Any())
        {
            if (targetGroup.LastNode is XText tail && string.IsNullOrWhiteSpace(tail.Value))
            {
                tail.AddBeforeSelf(new XText(newline + childIndent), pin);
            }
            else
            {
                targetGroup.Add(pin);
            }
        }
        else
        {
            if (
                targetGroup.FirstNode is XText lead
                && string.IsNullOrWhiteSpace(lead.Value)
            )
            {
                lead.Value = newline + childIndent;
            }

            targetGroup.Add(pin);
            targetGroup.Add(new XText(newline + groupIndent));
        }

        return Serialize(document, fileUsesCrlf);
    }

    private static string IndentOf(XText? node)
    {
        if (node is null || !string.IsNullOrWhiteSpace(node.Value))
        {
            return "  ";
        }

        var lastNewline = node.Value.LastIndexOf('\n');
        return lastNewline < 0 ? "  " : node.Value[(lastNewline + 1)..];
    }

    private static string Serialize(XDocument document, bool fileUsesCrlf)
    {
        var serialized = document.ToString();
        var serializedUsesCrlf = serialized.Contains("\r\n", StringComparison.Ordinal);
        if (fileUsesCrlf == serializedUsesCrlf)
        {
            return serialized;
        }

        return fileUsesCrlf
            ? serialized.Replace("\n", "\r\n")
            : serialized.Replace("\r\n", "\n");
    }

    private static bool IsConditional(XElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrEmpty(current.Attribute("Condition")?.Value) ||
                current.Name.LocalName.Equals("Otherwise", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
