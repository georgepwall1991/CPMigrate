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
            var (output, success) = await _dotNetCliService.RunListPackageAsync(projectDir, includeTransitive: true, vulnerable: false);

            if (!success)
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
            var (output, success) = await _dotNetCliService.RunListPackageAsync(projectDir, includeTransitive: true, vulnerable: true);

            if (!success)
            {
                return (new List<VulnerabilityInfo>(), false);
            }

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
            if (line.Contains("Top-level Package") || line.Contains("Updates"))
            {
                parsingTransitive = false;
                continue;
            }

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
                // Regex: 
                // Group 1: Package
                // Group 2: Severity
                // Group 3: Vulnerability
                // Group 4: Resolved
                // Group 5: Fixed in (Optional)
                var match = Regex.Match(line, @">\s*([^\s]+)\s+([^\s]+)\s+([^\s]+)\s+([^\s]+)(?:\s+([^\s]+))?", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (match.Success)
                {
                    var fixedIn = match.Groups[5].Success ? match.Groups[5].Value : "";
                    vulnerabilities.Add(new VulnerabilityInfo(
                        match.Groups[1].Value,
                        match.Groups[2].Value,
                        match.Groups[3].Value,
                        match.Groups[4].Value,
                        fixedIn, // Can be empty
                        projectName
                    ));
                }
            }
        }
        return vulnerabilities;
    }
}
