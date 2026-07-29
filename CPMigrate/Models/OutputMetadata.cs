using System.Reflection;

namespace CPMigrate.Models;

/// <summary>
/// Shared metadata for machine-readable output payloads.
/// </summary>
public static class OutputMetadata
{
    /// <summary>
    /// Schema version for JSON contract negotiation.
    /// </summary>
    /// <remarks>
    /// 1.1.0 additively introduced the bisect fields: <c>summary.packagesHeldBack</c>,
    /// <c>summary.verificationRuns</c>, <c>summary.bisectBudgetExhausted</c>, and
    /// <c>packageUpdates[].heldBack</c>.
    /// <para>
    /// 1.2.0 additively introduced the analysis-gate fields: <c>summary.failOnSeverity</c>,
    /// <c>summary.issuesAtOrAboveThreshold</c>, <c>summary.highestSeverity</c>,
    /// <c>summary.scanFailures</c>, <c>summary.deepScanFailures</c>, and
    /// <c>summary.issuesRemainingAfterFixes</c>. Together these let a
    /// consumer distinguish a clean run from one whose findings were below the gate, and either
    /// from one whose scan did not complete.
    /// </para>
    /// <para>
    /// 1.3.0 changed the meaning of one field: <c>analysisIssues[].affectedProjects</c> now holds
    /// each project's path relative to the scan root (<c>src/Api/Api.csproj</c>) rather than its
    /// file name (<c>Api.csproj</c>). Two projects can share a file name, so the old value could not
    /// identify a project — which made finding identity ambiguous and left SARIF location
    /// resolution guessing. Values are forward-slashed and contain no absolute paths, so they are
    /// identical on every machine.
    /// </para>
    /// This is the only field whose meaning has changed in any revision.
    /// </remarks>
    public const string SchemaVersion = "1.3.0";

    /// <summary>
    /// Gets the current CPMigrate application version at runtime.
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var informationalVersion = entryAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var plusIndex = informationalVersion.IndexOf('+');
                return plusIndex > 0 ? informationalVersion[..plusIndex] : informationalVersion;
            }

            return entryAssembly.GetName().Version?.ToString(3) ?? "unknown";
        }
    }
}
