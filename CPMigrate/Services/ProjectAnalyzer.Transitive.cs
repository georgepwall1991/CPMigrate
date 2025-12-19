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
        var references = new List<PackageReference>();
        var projectName = Path.GetFileName(projectFilePath);
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? ".";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "list package --include-transitive",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return (references, false);

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return (references, false);
            }

            // Parse the output of 'dotnet list package --include-transitive'
            // The output looks something like this:
            // Project 'ProjectName' has the following package references
            //    [net8.0]: 
            //    Top-level Package      Requested   Resolved
            //    > Newtonsoft.Json      13.0.1      13.0.1  
            //
            //    Transitive Package                                        Resolved
            //    > Microsoft.NETCore.Platforms                             1.1.0   
            
            var lines = output.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool parsingTransitive = false;

            foreach (var line in lines)
            {
                if (line.Contains("Transitive Package"))
                {
                    parsingTransitive = true;
                    continue;
                }

                if (parsingTransitive && line.Trim().StartsWith(">"))
                {
                    var match = Regex.Match(line, @">\s*([^\s]+)\s+([^\s]+)");
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

            return (references, true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan transitive dependencies for {projectName}: {ex.Message}");
            return (references, false);
        }
    }

    /// <summary>
    /// Scans a project for known vulnerabilities using 'dotnet list package --vulnerable --include-transitive'.
    /// </summary>
    public async Task<(List<VulnerabilityInfo> Vulnerabilities, bool Success)> ScanVulnerabilitiesAsync(string projectFilePath)
    {
        var vulnerabilities = new List<VulnerabilityInfo>();
        var projectName = Path.GetFileName(projectFilePath);
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? ".";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "list package --vulnerable --include-transitive",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return (vulnerabilities, false);

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse vulnerabilities
            // Output example:
            // Project 'ProjectName' has the following vulnerable packages
            //    [net8.0]: 
            //    Package                  Severity   Vulnerability      Resolved   Fixed in
            //    > System.Text.Json       High       GHSA-xxxx-xxxx     8.0.0      8.0.4   

            var lines = output.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool parsingPackages = false;

            foreach (var line in lines)
            {
                if (line.Contains("Package") && line.Contains("Severity"))
                {
                    parsingPackages = true;
                    continue;
                }

                if (parsingPackages && line.Trim().StartsWith(">"))
                {
                    // Use a more robust regex or split
                    var match = Regex.Match(line, @">\s*([^\s]+)\s+([^\s]+)\s+([^\s]+)\s+([^\s]+)\s+([^\s]+)");
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

            return (vulnerabilities, true);
        }
        catch (Exception ex)
        {
            _consoleService.Warning($"Could not scan vulnerabilities for {projectName}: {ex.Message}");
            return (vulnerabilities, false);
        }
    }
}
