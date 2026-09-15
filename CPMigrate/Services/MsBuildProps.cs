using System.Xml;
using System.Xml.Linq;

namespace CPMigrate.Services;

/// <summary>
/// Reads MSBuild props XML the way the engine evaluates it for the questions this tool asks:
/// document order, last assignment wins, unconditional <c>Import</c> elements followed with
/// <c>$(MSBuildThisFileDirectory)</c> anchored to the file that declares it, and upward walks
/// that stop at the repository root.
/// </summary>
internal static class MsBuildProps
{
    /// <summary>
    /// The property a repository sets to point central package management at a file of its own
    /// choosing instead of the conventional <c>Directory.Packages.props</c>.
    /// </summary>
    internal const string RedirectProperty = "DirectoryPackagesPropsPath";

    /// <summary>
    /// Whether a metadata value is an MSBuild expression — <c>$(prop)</c>, <c>@(item)</c>, or
    /// <c>%(metadata)</c> — rather than a literal. An expression is not a version: it cannot be
    /// compared, pinned over, or looked up on a feed, only evaluated in the scope that defines it.
    /// </summary>
    internal static bool IsExpressionValue(string? value)
    {
        return value is not null
            && (
                value.Contains("$(", StringComparison.Ordinal)
                || value.Contains("@(", StringComparison.Ordinal)
                || value.Contains("%(", StringComparison.Ordinal)
            );
    }

    private const string BuildPropsFileName = "Directory.Build.props";

    /// <summary>
    /// A <see cref="StringComparer"/> matching the case sensitivity of the filesystem under
    /// <paramref name="scanRoot"/>, probed empirically.
    ///
    /// <para>
    /// The operating system is not a sufficient proxy for filesystem semantics: macOS can use a
    /// case-sensitive APFS volume, and Windows supports case-sensitive directories. Probing beside
    /// the scan root keeps two distinct contexts such as <c>tools/</c> and <c>Tools/</c> distinct
    /// wherever the filesystem does. If the probe cannot run, ordinal comparison is the
    /// conservative fallback: it may inspect one case-insensitive path twice, but it cannot merge
    /// two contexts and silently lose one of their pins.
    /// </para>
    /// </summary>
    internal static StringComparer PathComparerFor(string? scanRoot)
    {
        if (string.IsNullOrWhiteSpace(scanRoot))
        {
            return StringComparer.Ordinal;
        }

        var root = Path.GetFullPath(scanRoot);
        if (!Directory.Exists(root))
        {
            return StringComparer.Ordinal;
        }

        var probeDirectory = Path.Combine(root, $".cpmigrate-case-probe-{Guid.NewGuid():N}");
        var probeFile = Path.Combine(probeDirectory, "probe");
        var differentlyCasedProbeFile = Path.Combine(probeDirectory, "PROBE");

        try
        {
            Directory.CreateDirectory(probeDirectory);
            File.WriteAllText(probeFile, string.Empty);

            return File.Exists(differentlyCasedProbeFile)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException
        )
        {
            return StringComparer.Ordinal;
        }
        finally
        {
            try
            {
                if (Directory.Exists(probeDirectory))
                {
                    Directory.Delete(probeDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or System.Security.SecurityException
            )
            {
                // A failed cleanup must not make an otherwise valid scan fail.
            }
        }
    }

    /// <summary>
    /// The props file a repository's <c>DirectoryPackagesPropsPath</c> declaration names.
    ///
    /// <para>
    /// The declaration is read from the nearest <c>Directory.Build.props</c>, following
    /// unconditional imports in evaluation order. Only <c>$(MSBuildThisFileDirectory)</c> is
    /// substituted — anchoring the path to the file that declares it is how this is written in
    /// practice — so <paramref name="declaredPath"/> is null when the value still holds a property
    /// this reader cannot evaluate.
    /// </para>
    /// </summary>
    /// <param name="startDirectory">Directory of the project whose props file is being resolved.</param>
    /// <param name="pathComparer">Filesystem case sensitivity, for the import-cycle guard.</param>
    /// <param name="declaredPath">
    /// The full path the declaration resolves to — whether or not the file exists — or null when
    /// the declaration could not be statically resolved.
    /// </param>
    /// <returns>True when a redirect was declared at all, resolvable or not.</returns>
    internal static bool TryGetDeclaredPropsPath(
        string startDirectory,
        StringComparer pathComparer,
        out string? declaredPath
    )
    {
        declaredPath = null;

        if (WalkUpForBuildProps(startDirectory) is not { } buildProps)
        {
            return false;
        }

        var (document, buildPropsPath) = buildProps;

        // Followed through imports, because delegating shared settings to an imported fragment is
        // ordinary and MSBuild observes the redirect wherever it is written. Reading only the outer
        // document fell back to the conventional file and judged the project against pins it never
        // receives.
        if (
            ReadPropertyThroughImports(
                document,
                buildPropsPath,
                RedirectProperty,
                new HashSet<string>(pathComparer),
                pathComparer
            )
            is not { } declaration
        )
        {
            return false;
        }

        // The directory of the file that *declared* it, not the outer one: a redirect anchored with
        // $(MSBuildThisFileDirectory) means its own file, and so does a bare relative path.
        var (declared, directory) = declaration;

        if (string.IsNullOrWhiteSpace(declared))
        {
            return false;
        }

        var expanded = declared
            .Replace(
                "$(MSBuildThisFileDirectory)",
                directory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return true;
        }

        declaredPath = Path.GetFullPath(
            Path.IsPathRooted(expanded) ? expanded : Path.Combine(directory, expanded)
        );

        return true;
    }

    /// <summary>
    /// The file <c>DirectoryPackagesPropsPath</c> names, when the nearest
    /// <c>Directory.Build.props</c> sets one.
    /// </summary>
    /// <param name="startDirectory">Directory of the project whose props file is being resolved.</param>
    /// <param name="resolved">The redirected file, or null when it could not be resolved.</param>
    /// <returns>True when a redirect was declared at all, resolved or not.</returns>
    internal static bool TryReadRedirectedPropsPath(
        string startDirectory,
        StringComparer pathComparer,
        out string? resolved
    )
    {
        resolved = null;

        if (!TryGetDeclaredPropsPath(startDirectory, pathComparer, out var declaredPath))
        {
            return false;
        }

        if (declaredPath is not null && File.Exists(declaredPath))
        {
            resolved = declaredPath;
        }

        return true;
    }

    /// <summary>
    /// Whether a directory is the root of a working tree.
    ///
    /// <c>.git</c> is a directory in an ordinary clone but a <em>file</em> in a linked worktree or a
    /// submodule. Testing only for the directory walked straight past those roots, so a props file
    /// in a parent could be picked up — the machine-dependent result this boundary exists to
    /// prevent, appearing only for the people using worktrees.
    /// </summary>
    internal static bool IsRepositoryRoot(string directory)
    {
        var git = Path.Combine(directory, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>
    /// The last assignment to <paramref name="propertyName"/> in a document, following
    /// unconditional imports in evaluation order, paired with the directory of the file that
    /// declared it.
    ///
    /// <para>
    /// An import that cannot be resolved by reading XML — conditioned, globbed, or built from
    /// properties — is skipped rather than followed: acting on an import that may not apply is
    /// worse than not reading it.
    /// </para>
    /// </summary>
    internal static (string Value, string Directory)? ReadPropertyThroughImports(
        XDocument document,
        string documentPath,
        string propertyName,
        HashSet<string> visited,
        StringComparer pathComparer
    )
    {
        var fullPath = Path.GetFullPath(documentPath);
        if (!visited.Add(fullPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
        {
            return null;
        }

        (string Value, string Directory)? resolved = null;

        // Document order, last assignment wins — how MSBuild evaluates a file. Reading the local
        // value first instead would let an import turn a property on but never off.
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                // Paired with the directory of the file that *declared* it, because a path-valued
                // property is anchored to its own file, not to whichever one imported it.
                resolved = (element.Value.Trim(), directory);
                continue;
            }

            if (!element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryResolveImportPath(element, directory, out var importedPath))
            {
                continue;
            }

            var imported = ReadProps(importedPath);
            if (imported is null)
            {
                continue;
            }

            if (
                ReadPropertyThroughImports(
                    imported,
                    importedPath,
                    propertyName,
                    visited,
                    pathComparer
                ) is
                { } inherited
            )
            {
                resolved = inherited;
            }
        }

        return resolved;
    }

    /// <summary>
    /// The file an <c>Import</c> names, when reading XML alone can say what it is.
    ///
    /// <para>
    /// <c>$(MSBuildThisFileDirectory)</c> is substituted rather than rejected: it is statically
    /// known, and anchoring an import with it is the ordinary way to write a portable one, so
    /// treating it as unresolvable left the commonest form unread. Any other property, or a glob,
    /// genuinely cannot be resolved here.
    /// </para>
    ///
    /// <para>
    /// A condition on the import <em>or on any element enclosing it</em> makes it unresolved for
    /// property readers. Central discovery may still ask for the statically resolvable path so it
    /// can retain declarations for <c>FloatingVersion</c>, while keeping them out of universal drift
    /// evidence. An <c>Import</c> inside a conditioned <c>ImportGroup</c> is as conditional as one
    /// carrying the attribute itself.
    /// </para>
    /// </summary>
    internal static bool TryResolveImportPath(
        XElement element,
        string directory,
        out string resolved,
        bool allowConditional = false
    )
    {
        resolved = string.Empty;

        if (!allowConditional && HasCondition(element))
        {
            return false;
        }

        var relative = element.Attribute("Project")?.Value;
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('*'))
        {
            return false;
        }

        var expanded = relative.Replace(
            "$(MSBuildThisFileDirectory)",
            directory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );

        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        resolved = Path.GetFullPath(
            Path.Combine(directory, expanded.Replace('\\', Path.DirectorySeparatorChar))
        );

        return true;
    }

    /// <summary>
    /// Whether the element — or any element enclosing it — carries a <c>Condition</c>, or sits
    /// inside an <c>Otherwise</c> branch.
    /// </summary>
    internal static bool HasCondition(XElement element)
    {
        for (XElement? enclosing = element; enclosing is not null; enclosing = enclosing.Parent)
        {
            if (
                enclosing.Name.LocalName.Equals("Otherwise", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(enclosing.Attribute("Condition")?.Value)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The nearest <c>Directory.Build.props</c> at or above a directory, stopping at the repository
    /// root for the same reason the central props walk does.
    /// </summary>
    /// <param name="startDirectory">Where to start looking.</param>
    /// <param name="single">When true, look only in <paramref name="startDirectory"/>.</param>
    internal static (XDocument Document, string Path)? WalkUpForBuildProps(
        string? startDirectory,
        bool single = false
    )
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            // The path travels with the document because reading a property through imports needs
            // to know which file it came from, both to resolve relative imports and to anchor a
            // path-valued property.
            var candidate = Path.Combine(directory.FullName, BuildPropsFileName);
            var found = ReadProps(candidate);
            if (found is not null)
            {
                return (found, candidate);
            }

            if (single || IsRepositoryRoot(directory.FullName))
            {
                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    internal static XDocument? ReadProps(string path)
    {
        try
        {
            return File.Exists(path) ? XDocument.Load(path) : null;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
