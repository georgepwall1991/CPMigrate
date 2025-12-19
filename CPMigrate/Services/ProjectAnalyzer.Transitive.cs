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
}
