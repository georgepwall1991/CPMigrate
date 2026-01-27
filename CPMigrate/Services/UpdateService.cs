using System.Reflection;
using System.Text.Json;
using CPMigrate.Models;
using NuGet.Versioning;
using Spectre.Console;

namespace CPMigrate.Services;

public class UpdateService : IUpdateService
{
    private const string PackageId = "CPMigrate";
    private readonly HttpClient _httpClient;
    private readonly IConsoleService _consoleService;

    public UpdateService(IConsoleService consoleService)
    {
        _consoleService = consoleService;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(3); // Fast timeout to not block UI
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "CPMigrate-CLI");
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
        catch (Exception)
        {
            // Silently fail for update checks to not annoy user
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
            var url = $"https://api.nuget.org/v3-flatcontainer/{PackageId.ToLower()}/index.json";
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
        catch
        {
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
                    using var process = new System.Diagnostics.Process();
                    process.StartInfo.FileName = "dotnet";
                    process.StartInfo.Arguments = $"tool update -g {PackageId}";
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    process.Start();
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
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
