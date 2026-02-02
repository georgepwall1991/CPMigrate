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
    public const string SchemaVersion = "1.0.0";

    /// <summary>
    /// Gets the current CPMigrate application version at runtime.
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var informationalVersion = entryAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var plusIndex = informationalVersion.IndexOf('+');
                return plusIndex > 0
                    ? informationalVersion[..plusIndex]
                    : informationalVersion;
            }

            return entryAssembly.GetName().Version?.ToString(3) ?? "unknown";
        }
    }
}
