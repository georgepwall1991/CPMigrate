using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CPMigrate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Spectre.Console;

namespace CPMigrate.Services;

public sealed class UpdateService : IUpdateService, IDisposable
{
    private const string PackageId = "CPMigrate";

    /// <summary>
    /// NuGet flat container API base URL used to check for available versions.
    /// </summary>
#pragma warning disable S1075 // URIs should not be hardcoded - NuGet public API is a stable URL
    private const string NuGetFlatContainerBaseUrl = "https://api.nuget.org/v3-flatcontainer";
#pragma warning restore S1075

    /// <summary>
    /// Timeout for update check HTTP requests. Kept short to avoid blocking the UI.
    /// </summary>
    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IConsoleService _consoleService;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(IConsoleService consoleService, HttpClient? httpClient = null, IProcessRunner? processRunner = null, ILogger<UpdateService>? logger = null)
    {
        _consoleService = consoleService;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        if (_ownsHttpClient)
        {
            _httpClient.Timeout = UpdateCheckTimeout;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "CPMigrate-CLI");
        }
        _processRunner = processRunner ?? new ProcessRunner();
        _logger = logger ?? NullLogger<UpdateService>.Instance;
    }

    public async Task<NuGetVersion?> CheckForUpdatesAsync()
    {
        try
        {
            var currentVersion = GetCurrentVersion();
            var latestVersion = await GetLatestVersionAsync();

            if (latestVersion != null && latestVersion > currentVersion)
            {
                return latestVersion;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background update check failed");
        }

        return null;
    }

    public async Task<SelfUpdateResult> PerformUpdateAsync(bool force = false, bool dryRun = false)
    {
        var currentVersion = GetCurrentVersion();

        _consoleService.Info($"Checking for updates... (Current: v{currentVersion})");

        var latestVersion = await GetLatestVersionAsync();

        if (latestVersion == null)
        {
            _consoleService.Warning("Could not fetch latest version info.");
            return new SelfUpdateResult("checkFailed", currentVersion.ToString());
        }

        if (latestVersion <= currentVersion)
        {
            _consoleService.Success($"You are already on the latest version (v{currentVersion}).");
            return new SelfUpdateResult("alreadyLatest", currentVersion.ToString(), latestVersion.ToString());
        }

        // --dry-run means what it means everywhere else: report, change nothing. Before it was
        // honoured here the flag was parsed and ignored — a real update ran under a flag whose
        // entire contract is that nothing changes.
        if (dryRun)
        {
            _consoleService.Info($"Dry run — v{latestVersion} is available; no changes made.");
            return new SelfUpdateResult("dryRun", currentVersion.ToString(), latestVersion.ToString());
        }

        _consoleService.Info($"Found new version: v{latestVersion}");

        // --force is the consent the prompt would otherwise collect — the same contract --prune
        // and --init already keep, and the only way this command runs unattended at all.
        if (!force)
        {
            // Replacing the running tool without consent is not something to infer from a
            // redirected stream; point at the unattended command instead.
            if (!_consoleService.IsInteractive)
            {
                _consoleService.Info("Cannot prompt on a non-interactive terminal. To update unattended, run:");
                _consoleService.Dim("  cpmigrate --update --force");
                _consoleService.Dim("  dotnet tool update --global CPMigrate");
                return new SelfUpdateResult("nonInteractive", currentVersion.ToString(), latestVersion.ToString());
            }

            if (!_consoleService.AskConfirmation("Do you want to update now?"))
            {
                return new SelfUpdateResult("declined", currentVersion.ToString(), latestVersion.ToString());
            }
        }

        return RunDotnetToolUpdate(currentVersion, latestVersion);
    }

    private static NuGetVersion GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var versionString = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly?.GetName().Version?.ToString()
            ?? "0.0.0";

        // Remove git hash if present (e.g. 1.0.0+hash)
        if (versionString.Contains('+'))
        {
            versionString = versionString.Split('+')[0];
        }

        return NuGetVersion.Parse(versionString);
    }

    private async Task<NuGetVersion?> GetLatestVersionAsync()
    {
        try
        {
            // Use NuGet flat container API for speed
            var url = $"{NuGetFlatContainerBaseUrl}/{PackageId.ToLower()}/index.json";
            var response = await _httpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("versions", out var versionsProp) &&
                versionsProp.ValueKind == JsonValueKind.Array)
            {
                var versions = versionsProp.EnumerateArray()
                    .Select(v => v.GetString())
                    .Where(v => v != null)
                    .Select(v => NuGetVersion.Parse(v!))
                    .OrderByDescending(v => v)
                    .ToList();

                return versions.FirstOrDefault(v => !v.IsPrerelease) ?? versions.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch latest version from NuGet");
            return null;
        }
        return null;
    }

    private SelfUpdateResult RunDotnetToolUpdate(NuGetVersion currentVersion, NuGetVersion latestVersion)
    {
        // The spinner renders through AnsiConsole itself, not the injected console — under a
        // machine-readable format (or any non-interactive run) it would write frames onto stdout
        // and break the document contract. Run the update plainly instead.
        if (!_consoleService.IsInteractive)
        {
            return ExecuteDotnetToolUpdate(currentVersion, latestVersion);
        }

        return AnsiConsole.Status()
            .Start("Updating CPMigrate...", _ => ExecuteDotnetToolUpdate(currentVersion, latestVersion));
    }

    private SelfUpdateResult ExecuteDotnetToolUpdate(NuGetVersion currentVersion, NuGetVersion latestVersion)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
#pragma warning disable S4036 // Suppress PATH warning: CLI tool intentionally uses dotnet from PATH
                FileName = "dotnet",
#pragma warning restore S4036
                Arguments = $"tool update -g {PackageId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var (exitCode, output, error) = _processRunner.Run(startInfo);

            if (exitCode == 0)
            {
                _consoleService.Success("Successfully updated CPMigrate! Please restart the tool.");
                return new SelfUpdateResult("updated", currentVersion.ToString(), latestVersion.ToString());
            }

            _consoleService.Error("Update failed:");
            _consoleService.Dim(error);
            _consoleService.Dim(output);
            return new SelfUpdateResult("failed", currentVersion.ToString(), latestVersion.ToString(), FirstNonEmpty(error, output));
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Update failed: {ex.Message}");
            return new SelfUpdateResult("failed", currentVersion.ToString(), latestVersion.ToString(), ex.Message);
        }
    }

    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
