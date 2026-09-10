using System.Xml.Linq;
using CPMigrate.Models;
using CPMigrate.Services.Migration;

namespace CPMigrate.Fixers;

/// <summary>
/// Sets <c>&lt;ManagePackageVersionsCentrally&gt;true&lt;/ManagePackageVersionsCentrally&gt;</c> in the
/// props file that already carries <c>PackageVersion</c> entries — the fix the
/// <c>CpmNotEnabled</c> finding's own text names. Refuses when any project still declares an
/// inline <c>Version</c>: enabling central management over inline versions turns every one into
/// NU1008, so the finding is real but the fix is <c>--migrate</c>, not this.
/// </summary>
public class CpmNotEnabledFixer : IFixer
{
    public string Name => "CPM Not Enabled Fixer";

    public bool CanFix(AnalysisIssue issue)
    {
        return issue.IssueCode == AnalysisIssueCode.CpmNotEnabled;
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, Options options, bool dryRun)
    {
        return Fix(issue, packageInfo, new FixRequest(MigrationValidator.GetOutputPaths(options).PropsPath, options.ConflictStrategy, dryRun));
    }

    public FixResult Fix(AnalysisIssue issue, ProjectPackageInfo packageInfo, FixRequest request)
    {
        // The finding names the props file in its metadata when it is not the conventional root
        // file — same resolution the orphaned-pin fixer keeps.
        var propsPath = issue.Metadata?.TryGetValue("propsFile", out var named) == true
            ? Path.Combine(packageInfo.BasePath ?? string.Empty, named)
            : Path.Combine(packageInfo.BasePath ?? string.Empty, "Directory.Packages.props");

        if (!File.Exists(propsPath))
        {
            return FixResult.Failed($"Props file not found: {propsPath}");
        }

        // Enabling CPM over a project that still declares Version inline turns every one into
        // NU1008 — the finding is real but the fix is --migrate, which strips them as it moves
        // the versions central. Refuse rather than break the restore. Version is the inline
        // declared version only — empty when absent — so a reference carrying only a
        // VersionOverride does not refuse: the override is the supported mechanism, and enabling
        // CPM is what activates it.
        var inlineVersions = packageInfo
            .GetDeclaredReferences()
            .Where(reference => !string.IsNullOrEmpty(reference.Version))
            .Select(reference => reference.ProjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (inlineVersions.Count > 0)
        {
            return FixResult.Failed(
                $"{inlineVersions.Count} project(s) still declare Version inline "
                    + $"({string.Join(", ", inlineVersions.Take(3))}"
                    + $"{(inlineVersions.Count > 3 ? ", …" : "")}) — enabling central management "
                    + "would turn each into NU1008. Run --migrate instead, which strips them as it "
                    + "moves the versions central."
            );
        }

        try
        {
            var originalContent = File.ReadAllText(propsPath);
            var doc = XDocument.Parse(originalContent);

            var property = doc
                .Descendants("ManagePackageVersionsCentrally")
                .FirstOrDefault();

            string before;
            if (property != null)
            {
                before = $"ManagePackageVersionsCentrally={property.Value}";
                property.Value = "true";
            }
            else
            {
                before = "no ManagePackageVersionsCentrally";
                var propertyGroup = doc.Root?.Element("PropertyGroup");
                if (propertyGroup == null)
                {
                    propertyGroup = new XElement("PropertyGroup");
                    doc.Root?.Add(propertyGroup);
                }

                propertyGroup.Add(new XElement("ManagePackageVersionsCentrally", "true"));
            }

            var newContent = doc.ToString();
            if (!request.DryRun)
            {
                File.WriteAllText(propsPath, newContent);
            }

            return FixResult.Succeeded(
                $"Enabled central package management in {Path.GetFileName(propsPath)}",
                new List<FileChange>
                {
                    new(propsPath, "Modified", before, "ManagePackageVersionsCentrally=true"),
                }
            );
        }
        catch (Exception ex)
        {
            throw new FixWriteException(propsPath, ex);
        }
    }
}
