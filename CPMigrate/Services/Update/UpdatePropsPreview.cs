using CPMigrate.Models;
using Microsoft.Build.Exceptions;

namespace CPMigrate.Services.Update;

/// <summary>
/// Renders the <c>Directory.Packages.props</c> an update or remediation pass would write, for a
/// dry run to preview or diff. The recipe is the real write's — baseline pins plus each applied
/// entry run through <see cref="PropsGenerator.MergeExisting"/>, the same call
/// <see cref="PropsUpdateTransaction"/> makes — so the preview is the actual outcome rather than
/// a re-derivation that can drift from it.
/// </summary>
internal static class UpdatePropsPreview
{
    /// <summary>
    /// Computes the props content that applying <paramref name="applied"/> on top of
    /// <paramref name="currentVersions"/> produces. Returns null when the props file cannot be
    /// rendered — a dry run then keeps its text summary rather than dying on a preview.
    /// </summary>
    public static string? PlannedContent(
        PropsGenerator propsGenerator,
        string propsPath,
        IReadOnlyDictionary<string, HashSet<string>> currentVersions,
        IEnumerable<PackageUpdateEntry> applied)
    {
        if (!File.Exists(propsPath))
        {
            return null;
        }

        var target = new Dictionary<string, HashSet<string>>(
            currentVersions, StringComparer.OrdinalIgnoreCase);
        foreach (var update in applied)
        {
            target[update.PackageName] = [update.LatestVersion];
        }

        try
        {
            var (content, _, _, _) = propsGenerator.MergeExisting(propsPath, target);
            return content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidProjectFileException or System.Xml.XmlException)
        {
            // A malformed or unreadable props file is reported by the real run's own failure
            // path — the preview is a courtesy, not another way to fail.
            return null;
        }
    }
}
