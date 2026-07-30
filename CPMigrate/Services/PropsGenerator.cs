using System.Security;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace CPMigrate.Services;

/// <summary>
/// Generates Directory.Packages.props content from collected package versions.
/// </summary>
public class PropsGenerator
{
    private const string PackageVersionItemType = "PackageVersion";
    private const string VersionMetadataName = "Version";

    /// <summary>
    /// How package IDs are ordered in a generated or merged props file.
    ///
    /// Explicit rather than <c>OrderBy(x =&gt; x.Key)</c>, whose default comparer is culture-sensitive.
    /// Directory.Packages.props is a committed file that several machines and a CI job all regenerate, so
    /// its line order is part of what gets diffed — and a line order that depends on the ambient culture,
    /// or on whether the host was built with invariant globalization, is not something to leave to
    /// chance. Case-insensitive first, because NuGet treats IDs case-insensitively and sorting purely by
    /// ordinal would strand every lower-cased ID in a block after the upper-cased ones. Ordinal breaks
    /// exact ties, which keeps the order total so it cannot depend on input enumeration order.
    /// </summary>
    internal static readonly IComparer<string> PackageIdOrder = Comparer<string>.Create(
        (left, right) =>
        {
            var caseInsensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return caseInsensitive != 0
                ? caseInsensitive
                : StringComparer.Ordinal.Compare(left, right);
        }
    );

    private readonly VersionResolver _versionResolver;

    public PropsGenerator(VersionResolver? versionResolver = null)
    {
        _versionResolver = versionResolver ?? new VersionResolver();
    }

    /// <summary>
    /// Generates the Directory.Packages.props XML content from collected package versions.
    /// Resolves version conflicts based on the specified strategy.
    /// </summary>
    /// <param name="packageVersions">Dictionary mapping package names to their version sets.</param>
    /// <param name="strategy">Strategy for resolving version conflicts.</param>
    /// <returns>Complete XML content for Directory.Packages.props file.</returns>
    public string Generate(
        Dictionary<string, HashSet<string>> packageVersions,
        ConflictStrategy strategy = ConflictStrategy.Highest
    )
    {
        var header = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
            """;

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine(header);

        foreach (var kvp in packageVersions.OrderBy(x => x.Key, PackageIdOrder))
        {
            // Skip packages with no versions (shouldn't happen, but defensive)
            if (kvp.Value.Count == 0)
            {
                continue;
            }

            // Resolve to single version if multiple exist
            var version =
                kvp.Value.Count > 1
                    ? _versionResolver.ResolveVersion(kvp.Value, strategy)
                    : kvp.Value.First();

            // XML-encode package name and version to prevent XML injection
            var safePackageName = SecurityElement.Escape(kvp.Key) ?? kvp.Key;
            var safeVersion = SecurityElement.Escape(version) ?? version;
            stringBuilder.AppendLine(
                $"""    <PackageVersion Include="{safePackageName}" Version="{safeVersion}" />"""
            );
        }

        stringBuilder.AppendLine(
            """
              </ItemGroup>
            </Project>
            """
        );
        return stringBuilder.ToString();
    }

    public static Dictionary<string, HashSet<string>> ReadExistingPackageVersions(
        string propsFilePath,
        out bool hasConditionalPackageVersions
    )
    {
        hasConditionalPackageVersions = false;
        Dictionary<string, HashSet<string>> packageVersions = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(propsFilePath))
        {
            throw new FileNotFoundException(
                $"Props file not found: {propsFilePath}",
                propsFilePath
            );
        }
        using var projectCollection = new ProjectCollection();
        var projectRoot = ProjectRootElement.Open(propsFilePath, projectCollection);

        foreach (var item in projectRoot.Items.Where(i => i.ItemType == PackageVersionItemType))
        {
            if (
                !string.IsNullOrEmpty(item.Condition)
                || !string.IsNullOrEmpty(item.Parent?.Condition)
            )
            {
                hasConditionalPackageVersions = true;
            }

            var packageName = !string.IsNullOrWhiteSpace(item.Include) ? item.Include : item.Update;
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            var version = GetMetadataValue(item, VersionMetadataName);
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (!packageVersions.TryGetValue(packageName, out var versions))
            {
                versions = [];
                packageVersions.Add(packageName, versions);
            }

            versions.Add(version);
        }

        return packageVersions;
    }

    public (
        string Content,
        int AddedCount,
        int UpdatedCount,
        bool HasConditionalPackageVersions
    ) MergeExisting(
        string propsFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        ConflictStrategy strategy = ConflictStrategy.Highest
    )
    {
        if (!File.Exists(propsFilePath))
        {
            throw new FileNotFoundException(
                $"Props file not found: {propsFilePath}",
                propsFilePath
            );
        }

        using var projectCollection = new ProjectCollection();
        var projectRoot = ProjectRootElement.Open(propsFilePath, projectCollection);
        var (itemsByPackage, hasConditionalPackageVersions) = BuildExistingItemsMap(projectRoot);

        EnsureManagePackageVersionsCentrally(projectRoot);

        var targetItemGroup = GetOrCreateTargetItemGroup(projectRoot);
        var documentedByComment = FindCommentedItems(targetItemGroup, propsFilePath);
        var (addedCount, updatedCount) = ProcessPackageVersions(
            packageVersions,
            strategy,
            itemsByPackage,
            targetItemGroup,
            keepOrdered: IsOrdered(targetItemGroup, documentedByComment),
            documentedByComment,
            expressVersionAsAttribute: PrefersAttributeForm(targetItemGroup)
        );

        return (projectRoot.RawXml, addedCount, updatedCount, hasConditionalPackageVersions);
    }

    private static (
        Dictionary<string, List<ProjectItemElement>> ItemsByPackage,
        bool HasConditionalVersions
    ) BuildExistingItemsMap(ProjectRootElement projectRoot)
    {
        Dictionary<string, List<ProjectItemElement>> itemsByPackage = [];
        var hasConditionalPackageVersions = false;

        foreach (var item in projectRoot.Items.Where(i => i.ItemType == PackageVersionItemType))
        {
            if (
                !string.IsNullOrEmpty(item.Condition)
                || !string.IsNullOrEmpty(item.Parent?.Condition)
            )
            {
                hasConditionalPackageVersions = true;
            }

            var packageName = GetPackageName(item);
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            AddToItemsMap(itemsByPackage, packageName, item);
        }

        return (itemsByPackage, hasConditionalPackageVersions);
    }

    private static string GetPackageName(ProjectItemElement item)
    {
        return !string.IsNullOrWhiteSpace(item.Include) ? item.Include : item.Update;
    }

    private static void AddToItemsMap(
        Dictionary<string, List<ProjectItemElement>> itemsByPackage,
        string packageName,
        ProjectItemElement item
    )
    {
        if (!itemsByPackage.TryGetValue(packageName, out var items))
        {
            items = [];
            itemsByPackage.Add(packageName, items);
        }

        items.Add(item);
    }

    private static ProjectItemGroupElement GetOrCreateTargetItemGroup(
        ProjectRootElement projectRoot
    )
    {
        return projectRoot.ItemGroups.FirstOrDefault(group =>
                string.IsNullOrEmpty(group.Condition)
                && group.Items.Any(item => item.ItemType == PackageVersionItemType)
            ) ?? projectRoot.AddItemGroup();
    }

    private (int AddedCount, int UpdatedCount) ProcessPackageVersions(
        Dictionary<string, HashSet<string>> packageVersions,
        ConflictStrategy strategy,
        Dictionary<string, List<ProjectItemElement>> itemsByPackage,
        ProjectItemGroupElement targetItemGroup,
        bool keepOrdered,
        HashSet<ProjectItemElement> documentedByComment,
        bool expressVersionAsAttribute
    )
    {
        var addedCount = 0;
        var updatedCount = 0;

        foreach (var kvp in packageVersions.OrderBy(k => k.Key, PackageIdOrder))
        {
            if (kvp.Value.Count == 0)
            {
                continue;
            }

            var (added, updated) = ProcessSinglePackageVersion(
                kvp.Key,
                kvp.Value,
                strategy,
                itemsByPackage,
                targetItemGroup,
                keepOrdered,
                documentedByComment,
                expressVersionAsAttribute
            );
            addedCount += added;
            updatedCount += updated;
        }

        return (addedCount, updatedCount);
    }

    private (int Added, int Updated) ProcessSinglePackageVersion(
        string packageName,
        HashSet<string> versions,
        ConflictStrategy strategy,
        Dictionary<string, List<ProjectItemElement>> itemsByPackage,
        ProjectItemGroupElement targetItemGroup,
        bool keepOrdered,
        HashSet<ProjectItemElement> documentedByComment,
        bool expressVersionAsAttribute
    )
    {
        var resolvedVersion = ResolvePackageVersion(versions, strategy);

        if (itemsByPackage.TryGetValue(packageName, out var existingItems))
        {
            if (ShouldSkipUpdateForConditionalItems(existingItems, versions))
            {
                return (0, 0);
            }

            return UpdateExistingItems(existingItems, resolvedVersion) ? (0, 1) : (0, 0);
        }

        AddNewPackageVersion(
            targetItemGroup,
            packageName,
            resolvedVersion,
            keepOrdered,
            documentedByComment,
            expressVersionAsAttribute
        );
        return (1, 0);
    }

    private static bool ShouldSkipUpdateForConditionalItems(
        List<ProjectItemElement> existingItems,
        HashSet<string> versions
    )
    {
        if (existingItems.Count <= 1)
        {
            return false;
        }

        var existingVersions = existingItems
            .Select(item => GetMetadataValue(item, VersionMetadataName))
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToHashSet();

        return versions.IsSubsetOf(existingVersions);
    }

    private string ResolvePackageVersion(HashSet<string> versions, ConflictStrategy strategy)
    {
        return versions.Count > 1
            ? _versionResolver.ResolveVersion(versions, strategy)
            : versions.First();
    }

    private static bool UpdateExistingItems(List<ProjectItemElement> items, string resolvedVersion)
    {
        var updated = false;

        foreach (var item in items)
        {
            var currentVersion = GetMetadataValue(item, VersionMetadataName);
            if (!string.Equals(currentVersion, resolvedVersion, StringComparison.OrdinalIgnoreCase))
            {
                SetMetadataValue(item, VersionMetadataName, resolvedVersion);
                updated = true;
            }
        }

        return updated;
    }

    /// <summary>
    /// Adds a pin, placed so that a file which was ordered stays ordered.
    ///
    /// Not <c>AddItemGroup.AddItem</c>, which chooses its own position: given an *unordered* group it
    /// inserted at the top, and given an ordered one it inserted between a comment and the item that
    /// comment documents — silently reattaching a team's "pinned because 2.x drops netstandard2.0" note
    /// to a different package. That is worse than losing the comment, because the file still reads as if
    /// it were right. MSBuild's object model does not expose comments at all (they survive only because
    /// the underlying document is round-tripped), so the position is chosen relative to the *preceding*
    /// item instead, which leaves any comment attached to the following one where it was.
    /// </summary>
    private static void AddNewPackageVersion(
        ProjectItemGroupElement targetItemGroup,
        string packageName,
        string version,
        bool keepOrdered,
        HashSet<ProjectItemElement> documentedByComment,
        bool expressVersionAsAttribute
    )
    {
        var newItem = targetItemGroup.ContainingProject.CreateItemElement(
            PackageVersionItemType,
            packageName
        );

        var existing = targetItemGroup
            .Items.Where(item => item.ItemType == PackageVersionItemType)
            .ToList();

        if (!keepOrdered)
        {
            // An unordered group is left as its author arranged it: reordering to match our preference
            // would produce a diff far larger than the change that was asked for, and would move
            // comments away from what they document. The new pin goes at the end.
            targetItemGroup.AppendChild(newItem);
            SetMetadataValue(newItem, VersionMetadataName, version, expressVersionAsAttribute);
            return;
        }

        var precedingIndex = existing.FindLastIndex(item =>
            PackageIdOrder.Compare(GetPackageName(item), packageName) < 0
        );

        // Every MSBuild insertion lands immediately before the *next item*, which is to say after that
        // item's comment — the model cannot address a position earlier than that, because it cannot see
        // comments at all. So where the sorted position would take a comment away from the entry it
        // documents, the new pin goes one slot later instead. One entry marginally out of order is a
        // strictly smaller problem than an explanation silently attached to the wrong package.
        while (
            precedingIndex + 1 < existing.Count
            && documentedByComment.Contains(existing[precedingIndex + 1])
        )
        {
            precedingIndex++;
        }

        if (precedingIndex >= 0)
        {
            targetItemGroup.InsertAfterChild(newItem, existing[precedingIndex]);
        }
        else if (existing.Count > 0)
        {
            // Sorts before everything already there. A comment that is the group's first child reads as
            // a header for the group rather than for one entry, and inserting before the first item
            // leaves it where its author put it.
            targetItemGroup.InsertBeforeChild(newItem, existing[0]);
        }
        else
        {
            targetItemGroup.AppendChild(newItem);
        }

        SetMetadataValue(newItem, VersionMetadataName, version, expressVersionAsAttribute);
    }

    /// <summary>
    /// Whether the group's existing pins are already in <see cref="PackageIdOrder"/>. Evaluated once,
    /// before anything is inserted, so adding several packages in one run cannot change the answer
    /// partway through and place some of them by one rule and some by another.
    /// </summary>
    /// <summary>
    /// The pins that have a comment immediately above them, and therefore an explanation that must not
    /// be handed to a different package.
    ///
    /// Read from the file's own lines because MSBuild's object model does not expose comments — they
    /// survive a merge only because the underlying document is round-tripped whole, which is also why
    /// nothing in the model can be positioned relative to one. A comment that is the group's first child
    /// is treated as a header for the group rather than for the entry below it, so it is not counted:
    /// there is no way to tell the two apart, and holding the first slot open for a header is the
    /// reading that leaves an ordered file ordered.
    /// </summary>
    private static HashSet<ProjectItemElement> FindCommentedItems(
        ProjectItemGroupElement itemGroup,
        string propsFilePath
    )
    {
        HashSet<ProjectItemElement> documented = [];

        string[] lines;
        try
        {
            lines = File.ReadAllLines(propsFilePath);
        }
        catch (IOException)
        {
            // Without the source text every position is treated as comment-free, which is the same
            // behaviour as a file that has no comments. Failing the merge over this would be worse.
            return documented;
        }

        var items = itemGroup.Items.Where(item => item.ItemType == PackageVersionItemType).ToList();

        for (var index = 0; index < items.Count; index++)
        {
            // Location.Line is 1-based, and the first entry's comment is the group header case above.
            var above = items[index].Location.Line - 2;
            while (above >= 0 && string.IsNullOrWhiteSpace(lines[above]))
            {
                above--;
            }

            if (above < 0 || index == 0)
            {
                continue;
            }

            var text = lines[above].Trim();
            if (
                text.StartsWith("<!--", StringComparison.Ordinal)
                || text.EndsWith("-->", StringComparison.Ordinal)
            )
            {
                documented.Add(items[index]);
            }
        }

        return documented;
    }

    /// <summary>
    /// Whether the group's existing pins are in <see cref="PackageIdOrder"/>, and so whether a new pin
    /// should be sorted into place or appended.
    ///
    /// Honouring a comment costs one entry its exact position, so a plain ordering check would read the
    /// position <see cref="AddNewPackageVersion"/> had itself just forced as evidence the file was
    /// unsorted, give up, and append everything from then on — one comment would permanently degrade the
    /// file. The exemption is therefore not "ignore any inversion after a commented entry", which would
    /// hide inversions that have nothing to do with a comment and treat a hand-arranged file as sorted.
    /// Instead the sequence is normalised by undoing exactly the displacement this class creates — moving
    /// a pin back in front of the single commented entry it was pushed past — and the result must then be
    /// ordered with no exceptions at all.
    ///
    /// One case is genuinely undecidable: <c>Alpha, &lt;!--why--&gt; Zulu, Bravo</c> is byte-identical to
    /// what inserting Bravo into <c>Alpha, &lt;!--why--&gt; Zulu</c> produces, so no rule can tell a
    /// hand-written file of that shape from this class's own output. It is read as ordered, which is the
    /// benign reading: the worst outcome is that a later pin is sorted into a file whose author did not
    /// ask for sorting.
    /// </summary>
    private static bool IsOrdered(
        ProjectItemGroupElement itemGroup,
        HashSet<ProjectItemElement> documentedByComment
    )
    {
        var items = itemGroup.Items.Where(item => item.ItemType == PackageVersionItemType).ToList();
        List<string> normalized = [];

        var index = 0;
        while (index < items.Count)
        {
            var name = GetPackageName(items[index]);

            // A commented entry immediately followed by one that sorts before it is the displacement
            // this class creates. Undo it, and consume both.
            if (
                documentedByComment.Contains(items[index])
                && index + 1 < items.Count
                && PackageIdOrder.Compare(name, GetPackageName(items[index + 1])) > 0
            )
            {
                normalized.Add(GetPackageName(items[index + 1]));
                normalized.Add(name);
                index += 2;
                continue;
            }

            normalized.Add(name);
            index++;
        }

        for (var i = 1; i < normalized.Count; i++)
        {
            if (PackageIdOrder.Compare(normalized[i - 1], normalized[i]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureManagePackageVersionsCentrally(ProjectRootElement projectRoot)
    {
        var hasProperty = projectRoot.Properties.Any(p =>
            p.Name == "ManagePackageVersionsCentrally"
        );
        if (hasProperty)
        {
            return;
        }

        var propertyGroup =
            projectRoot.PropertyGroups.FirstOrDefault(group =>
                string.IsNullOrEmpty(group.Condition)
            ) ?? projectRoot.AddPropertyGroup();

        propertyGroup.AddProperty("ManagePackageVersionsCentrally", "true");
    }

    private static string? GetMetadataValue(ProjectItemElement item, string name)
    {
        var metadata = item.Metadata.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
        );
        return metadata?.Value;
    }

    private static void SetMetadataValue(
        ProjectItemElement item,
        string name,
        string value,
        bool expressAsAttribute = true
    )
    {
        var metadata = item.Metadata.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
        );
        if (metadata != null)
        {
            // Updated in place, keeping whatever style it was written in. Rewriting an entry someone
            // formatted deliberately would be a diff they did not ask for.
            metadata.Value = value;
            return;
        }

        // AddMetadata defaults to writing the version as a child element regardless of what the file
        // around it looks like, so a merge produced new pins in that form next to entries carrying a
        // Version attribute: one file, two styles for the same thing, and a three-line diff for a
        // one-line addition. The caller passes the style the file actually uses.
        item.AddMetadata(name, value, expressAsAttribute);
    }

    /// <summary>
    /// Whether new pins should write their version as an attribute, decided by what the group already
    /// does rather than by our preference.
    ///
    /// Attribute form for an empty group, and for a group that already mixes the two — that is what
    /// <see cref="Generate"/> emits and what NuGet's own documentation uses, so it is the right default
    /// when the file expresses no opinion. But a group written *consistently* in element form has an
    /// opinion, and adding attribute-form entries to it would recreate exactly the mixture this is meant
    /// to avoid, only in the other direction.
    /// </summary>
    private static bool PrefersAttributeForm(ProjectItemGroupElement itemGroup)
    {
        var versions = itemGroup
            .Items.Where(item => item.ItemType == PackageVersionItemType)
            .Select(item =>
                item.Metadata.FirstOrDefault(m =>
                    string.Equals(m.Name, VersionMetadataName, StringComparison.OrdinalIgnoreCase)
                )
            )
            .Where(metadata => metadata is not null)
            .ToList();

        return versions.Count == 0 || versions.Any(metadata => metadata!.ExpressedAsAttribute);
    }
}
