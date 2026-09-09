using System.Text.Json;

namespace CPMigrate.Services;

/// <summary>
/// Privacy-first opt-in telemetry recorder.
/// Captures only command-level operational metrics and no repository/package/file content.
/// </summary>
public static class TelemetryService
{
    private const string OptInEnvironmentVariable = "CPMIGRATE_TELEMETRY_OPT_IN";

    public static void RecordCommandRun(Options options, int exitCode, TimeSpan duration)
    {
        if (!IsEnabled())
        {
            return;
        }

        try
        {
            RecordEvent(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), options, exitCode, duration);
        }
        catch
        {
            // Telemetry must never impact command execution.
        }
    }

    /// <summary>
    /// Writes one telemetry event under <paramref name="userProfileDir"/>. Internal so tests can
    /// isolate the destination without depending on how each OS resolves the user profile
    /// (<c>HOME</c> on Unix, the profile lookup on Windows). An empty directory means home is
    /// unresolvable (containers, service accounts): record nothing rather than scattering a
    /// <c>.cpmigrate</c> directory into the working folder.
    /// </summary>
    internal static void RecordEvent(string? userProfileDir, Options options, int exitCode, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(userProfileDir))
        {
            return;
        }

        var telemetryDir = Path.Combine(userProfileDir, ".cpmigrate", "telemetry");
        Directory.CreateDirectory(telemetryDir);
        var payload = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            operation = GetOperation(options),
            success = exitCode == ExitCodes.Success,
            exitCode,
            exitCodeCategory = GetExitCodeCategory(exitCode),
            durationMs = (long)duration.TotalMilliseconds,
            flags = new
            {
                options.DryRun,
                options.Quiet,
                json = options.Output == OutputFormat.Json,
                options.Analyze,
                options.AuditSecurity,
                options.AnalyzeOutdated,
                options.AnalyzeDeprecated,
                options.AnalyzeLicenses,
                options.Fix,
                options.FixDryRun,
                options.IncludeTransitive,
                options.UpdatePackages,
                options.Rollback,
                batch = !string.IsNullOrWhiteSpace(options.BatchDir)
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var file = Path.Combine(telemetryDir, "events.ndjson");
        File.AppendAllText(file, json + Environment.NewLine);
    }

    internal static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(OptInEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOperation(Options options)
    {
        if (options.UpdatePackages)
        {
            return "update-packages";
        }

        if (options.Update)
        {
            return "update";
        }

        if (options.Rollback)
        {
            return "rollback";
        }

        if (options.Analyze)
        {
            return "analyze";
        }

        if (options.UnifyProps)
        {
            return "unify-props";
        }

        if (!string.IsNullOrWhiteSpace(options.BatchDir))
        {
            return "batch";
        }

        if (options.PruneAll)
        {
            return "prune-all";
        }

        if (options.PruneBackups)
        {
            return "prune-backups";
        }

        if (options.ListBackups)
        {
            return "list-backups";
        }

        return "migrate";
    }

    private static string GetExitCodeCategory(int exitCode)
    {
        return exitCode switch
        {
            ExitCodes.Success => "success",
            ExitCodes.ValidationError => "validation",
            ExitCodes.FileOperationError => "file-operation",
            ExitCodes.VersionConflict => "version-conflict",
            ExitCodes.NoProjectsFound => "no-projects",
            ExitCodes.AnalysisIssuesFound => "analysis-issues",
            ExitCodes.UnexpectedError => "unexpected",
            ExitCodes.TestFailure => "test-failure",
            _ => "other"
        };
    }
}
