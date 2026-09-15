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

        var originalContent = request.ReadFile(propsPath);
        var hasActivePackagePin = false;
        XDocument? propsDocument = null;
        List<XElement>? targetPins = null;

        try
        {
            propsDocument = XDocument.Parse(originalContent, LoadOptions.PreserveWhitespace);
            targetPins = propsDocument
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
            // Update through the parsed document, not text: a regex on the raw file only matched
            // <Include="X" Version="…"> in that exact order, and only the first <Version> child —
            // while item metadata is last-wins, so a trailing declaration kept pinning the old
            // version under a "Pinned" report. Every Version declaration on every matching pin is
            // set, in whichever form the file wrote it.
            foreach (var pin in targetPins)
            {
                var versionAttribute = pin.Attributes().FirstOrDefault(a =>
                    a.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase));
                if (versionAttribute is not null)
                {
                    versionAttribute.Value = bestVersion;
                }

                foreach (
                    var versionElement in pin.Elements().Where(e =>
                        e.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase))
                )
                {
                    versionElement.Value = bestVersion;
                }
            }

            updatedContent = Serialize(
                propsDocument,
                originalContent.Contains("\r\n", StringComparison.Ordinal)
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

        request.WriteFile(propsPath, updatedContent);

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

        // Search and construct in the project's own namespace: the default MSBuild namespace makes an
        // unqualified search find nothing, and unqualified additions would serialize with xmlns="" —
        // elements MSBuild ignores while the fix reports success.
        var ns = root.Name.Namespace;
        var fileUsesCrlf = originalContent.Contains("\r\n", StringComparison.Ordinal);
        var newline = fileUsesCrlf ? "\r\n" : "\n";
        var pin = new XElement(
            ns + "PackageVersion",
            new XAttribute("Include", packageName),
            new XAttribute("Version", bestVersion)
        );

        var targetGroup = root
            .Descendants(ns + "ItemGroup")
            .Where(group => group.Parent == root)
            .FirstOrDefault(group => !IsConditional(group));

        if (targetGroup is null)
        {
            var itemGroup = new XElement(
                ns + "ItemGroup",
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
        var newline = fileUsesCrlf ? "\r\n" : "\n";
        var serialized = document.ToString();
        if (document.Declaration is not null)
        {
            // ToString omits the <?xml …?> declaration a props file can carry — re-emit it rather
            // than silently dropping a line the file had.
            serialized = document.Declaration.ToString() + newline + serialized;
        }

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
