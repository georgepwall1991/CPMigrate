using System.Diagnostics;
using System.Text.RegularExpressions;
using CPMigrate.Models;

namespace CPMigrate.Services;

public partial class ProjectAnalyzer
{
    /// <summary>
    /// Scans a project for transitive dependencies using 'dotnet list package --include-transitive'.
    /// Requires the project to be restored.
    /// </summary>
    public async Task<(List<PackageReference> References, bool Success)> ScanTransitivePackagesAsync(string projectFilePath)
    {
        var projectName = Path.GetFileName(projectFilePath);
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? ".";

        try
        {
            var startInfo = new ProcessStartInfo
            {
#pragma warning disable S4036 // Suppress PATH warning: CLI tool intentionally uses dotnet from PATH
                FileName = "dotnet",
#pragma warning restore S4036
                Arguments = "list package --include-transitive",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (new List<PackageReference>(), false);
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return (new List<PackageReference>(), false);
            }

            var references = ParseTransitivePackages(output, projectFilePath, projectName);
            return (references, true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan transitive dependencies for {projectName}: {ex.Message}");
            return (new List<PackageReference>(), false);
        }
    }

    /// <summary>
    /// Scans a project for known vulnerabilities using 'dotnet list package --vulnerable --include-transitive'.
    /// </summary>
    public async Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(string projectFilePath)
    {
        var projectName = Path.GetFileName(projectFilePath);
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? ".";

        try
        {
            var startInfo = new ProcessStartInfo
            {
#pragma warning disable S4036 // Suppress PATH warning: CLI tool intentionally uses dotnet from PATH
                FileName = "dotnet",
#pragma warning restore S4036
                Arguments = "list package --vulnerable --include-transitive",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (new List<VulnerabilityInfo>(), false);
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var vulnerabilities = ParseVulnerabilities(output, projectName);
            return (vulnerabilities, true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan vulnerabilities for {projectName}: {ex.Message}");
            return (new List<VulnerabilityInfo>(), false);
        }
    }

    internal static List<PackageReference> ParseTransitivePackages(string output, string projectFilePath, string projectName)
    {
        var references = new List<PackageReference>();
        var lines = output.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
        bool parsingTransitive = false;

        foreach (var line in lines)
        {
            if (line.Contains("Transitive Package"))
            {
                parsingTransitive = true;
                continue;
            }

            if (parsingTransitive && line.Trim().StartsWith('>'))
            {
                var match = Regex.Match(line, @">\s*([^\s]+)\s+([^\s]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (match.Success)
                {
                    var packageName = match.Groups[1].Value;
                    var resolvedVersion = match.Groups[2].Value;

                    references.Add(new PackageReference(
                        packageName,
                        resolvedVersion,
                        projectFilePath,
                        projectName,
                        IsTransitive: true
                    ));
                }
            }
        }
        return references;
    }

    internal static List<VulnerabilityInfo> ParseVulnerabilities(string output, string projectName)
    {
        var vulnerabilities = new List<VulnerabilityInfo>();
        var lines = output.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
        bool parsingPackages = false;

        foreach (var line in lines)
        {
            if (line.Contains("Package") && line.Contains("Severity"))
            {
                parsingPackages = true;
                continue;
            }

            if (parsingPackages && line.Trim().StartsWith('>'))
            {
                var match = Regex.Match(line, @">\s*([^\s]+)\s+([^\s]+)\s+([^\s]+)\s+([^\s]+)\s+([^\s]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (match.Success)
                {
                    vulnerabilities.Add(new VulnerabilityInfo(
                        match.Groups[1].Value,
                        match.Groups[2].Value,
                        match.Groups[3].Value,
                        match.Groups[4].Value,
                        match.Groups[5].Value,
                        projectName
                    ));
                }
            }
        }
        return vulnerabilities;
    }
}
