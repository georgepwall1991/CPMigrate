using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CPMigrate.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Spectre.Console;

namespace CPMigrate.Services;

public class UpdateService : IUpdateService
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
    private readonly IConsoleService _consoleService;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(IConsoleService consoleService, HttpClient? httpClient = null, IProcessRunner? processRunner = null, ILogger<UpdateService>? logger = null)
    {
        _consoleService = consoleService;
        _httpClient = httpClient ?? new HttpClient();
        if (httpClient == null)
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

    public async Task<bool> PerformUpdateAsync()
    {
        var currentVersion = GetCurrentVersion();

        _consoleService.Info($"Checking for updates... (Current: v{currentVersion})");

        var latestVersion = await GetLatestVersionAsync();

        if (latestVersion == null)
        {
            _consoleService.Warning("Could not fetch latest version info.");
            return false;
        }

        if (latestVersion <= currentVersion)
        {
            _consoleService.Success($"You are already on the latest version (v{currentVersion}).");
            return true;
        }

        _consoleService.Info($"Found new version: v{latestVersion}");
        if (!_consoleService.AskConfirmation("Do you want to update now?"))
        {
            return false;
        }

        return RunDotnetToolUpdate();
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

    private bool RunDotnetToolUpdate()
    {
        // Try global update first
        return AnsiConsole.Status()
            .Start("Updating CPMigrate...", ctx =>
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
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
                        return true;
                    }
                    else
                    {
                        _consoleService.Error("Update failed:");
                        _consoleService.Dim(error);
                        _consoleService.Dim(output);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _consoleService.Error($"Update failed: {ex.Message}");
                    return false;
                }
            });
    }
}
