using System.ComponentModel;
using System.Diagnostics;

namespace CPMigrate.Services;

/// <summary>
/// Implementation of .NET CLI interactions.
/// </summary>
public class DotNetCliService : IDotNetCliService
{
    public async Task<(string Output, bool Success)> RunListPackageAsync(string projectPathOrDirectory, bool includeTransitive, bool vulnerable)
    {
        var options = new DotNetPackageListOptions
        {
            IncludeTransitive = includeTransitive,
            Vulnerable = vulnerable
        };

        return await RunPackageListJsonAsync(projectPathOrDirectory, options);
    }

    public async Task<(string Output, bool Success)> RunPackageListJsonAsync(string projectPathOrDirectory, DotNetPackageListOptions options)
    {
        var target = ResolveProjectListTarget(projectPathOrDirectory);
        // Primary command for stable SDKs: dotnet list [project] package
        var args = BuildListPackageArguments(options, target.TargetArgument);
        var (stdOut, stdErr, success) = await RunDotNetCommandDetailedAsync(
            args,
            target.WorkingDirectory,
            options.IsolatedIntermediateDirectory);
        if (success)
        {
            return (stdOut, true);
        }

        // Compatibility fallback for SDKs/preview channels that expose `dotnet package list`.
        var combined = CombineOutput(stdOut, stdErr);
        if (ShouldTryPackageVerbFallback(combined))
        {
            var packageVerbArgs = BuildPackageListArguments(options, target.TargetArgument);
            var (packageVerbOut, packageVerbErr, packageVerbSuccess) = await RunDotNetCommandDetailedAsync(
                packageVerbArgs,
                target.WorkingDirectory,
                options.IsolatedIntermediateDirectory);
            if (packageVerbSuccess)
            {
                return (packageVerbOut, true);
            }

            return (CombineOutput(packageVerbOut, packageVerbErr), false);
        }

        return (combined, false);
    }

    public async Task<(string Output, bool Success)> RunRestoreAsync(string solutionOrProjectPath)
    {
        return await RunDotNetCommandAsync($"restore \"{solutionOrProjectPath}\"", Path.GetDirectoryName(solutionOrProjectPath) ?? ".");
    }

    public async Task<(string Output, bool Success)> RunNugetListSourceAsync(string workingDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        if (File.Exists(directory))
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(directory)) ?? ".";
        }

        // Detailed, not Short: Short is name-only, and the doctor has to probe each HTTP source.
        return await RunDotNetCommandAsync("nuget list source --format Detailed", directory);
    }

    public async Task<(string Output, bool Success)> RunTestAsync(string solutionOrProjectPath, string? testFilter = null)
    {
        return await RunDotNetCommandAsync(
            BuildTestArguments(solutionOrProjectPath, testFilter),
            Path.GetDirectoryName(solutionOrProjectPath) ?? ".");
    }

    internal static string BuildTestArguments(string solutionOrProjectPath, string? testFilter)
    {
        var args = $"test \"{solutionOrProjectPath}\" --no-restore";

        if (!string.IsNullOrWhiteSpace(testFilter))
        {
            // Escape embedded quotes so a filter expression cannot break out of the argument.
            args += $" --filter \"{testFilter.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        return args;
    }

    internal static string BuildListPackageArguments(DotNetPackageListOptions options, string? targetArgument)
    {
        var args = "list";

        if (!string.IsNullOrWhiteSpace(targetArgument))
        {
            args += $" {targetArgument}";
        }

        args += " package --format json --output-version 1";

        if (options.Vulnerable)
        {
            args += " --vulnerable";
        }
        else if (options.Outdated)
        {
            args += " --outdated";
        }
        else if (options.Deprecated)
        {
            args += " --deprecated";
        }

        if (options.IncludeTransitive)
        {
            args += " --include-transitive";
        }

        if (options.IncludePrerelease && (options.Outdated || options.Deprecated))
        {
            args += " --include-prerelease";
        }

        return args;
    }

    internal static string BuildPackageListArguments(DotNetPackageListOptions options, string? targetArgument)
    {
        var args = "package list";

        if (!string.IsNullOrWhiteSpace(targetArgument))
        {
            args += $" --project {targetArgument}";
        }

        args += " --format json --output-version 1";

        if (options.Vulnerable)
        {
            args += " --vulnerable";
        }
        else if (options.Outdated)
        {
            args += " --outdated";
        }
        else if (options.Deprecated)
        {
            args += " --deprecated";
        }

        if (options.IncludeTransitive)
        {
            args += " --include-transitive";
        }

        if (options.IncludePrerelease && (options.Outdated || options.Deprecated))
        {
            args += " --include-prerelease";
        }

        return args;
    }

    internal static bool ShouldTryPackageVerbFallback(string output)
    {
        return output.Contains("Unrecognized command or argument 'list'", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Unrecognized command or argument 'package'", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("No executable found matching command \"dotnet-list\"", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Unknown command 'list'", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Unknown command 'package'", StringComparison.OrdinalIgnoreCase);
    }

    internal static string CombineOutput(string stdOut, string stdErr)
    {
        return string.IsNullOrEmpty(stdErr) ? stdOut : $"{stdOut}\n{stdErr}";
    }

    internal static (string? TargetArgument, string WorkingDirectory) ResolveProjectListTarget(string projectPathOrDirectory)
    {
        if (File.Exists(projectPathOrDirectory))
        {
            var fullPath = Path.GetFullPath(projectPathOrDirectory);
            var workingDirectory = Path.GetDirectoryName(fullPath) ?? ".";
            return ($"\"{fullPath}\"", workingDirectory);
        }

        var directory = string.IsNullOrWhiteSpace(projectPathOrDirectory) ? "." : projectPathOrDirectory;
        var fullDirectoryPath = Path.GetFullPath(directory);
        return (null, fullDirectoryPath);
    }

    private static async Task<(string Output, bool Success)> RunDotNetCommandAsync(string arguments, string workingDirectory)
    {
        var (stdOut, stdErr, success) = await RunDotNetCommandDetailedAsync(arguments, workingDirectory);
        return (CombineOutput(stdOut, stdErr), success);
    }

    private static async Task<(string StdOut, string StdErr, bool Success)> RunDotNetCommandDetailedAsync(
        string arguments,
        string workingDirectory,
        string? isolatedIntermediateDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
#pragma warning disable S4036 // Suppress PATH warning: CLI tool intentionally uses dotnet from PATH
            FileName = "dotnet",
#pragma warning restore S4036
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(isolatedIntermediateDirectory))
        {
            // Environment variables, because `dotnet package list` rejects -p: arguments while MSBuild still
            // reads the environment as properties. The trailing separator matters: MSBuild treats these as
            // directory prefixes and concatenates onto them.
            var directory = isolatedIntermediateDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ) + Path.DirectorySeparatorChar;

            // MSBuildProjectExtensionsPath is the one that actually decides where project.assets.json goes,
            // and it is the one that holds. BaseIntermediateOutputPath alone is not enough: a repo-wide
            // artifacts/obj set in Directory.Build.props is assigned *before* the default logic runs, so it
            // overwrites the environment and two projects end up sharing an assets file again — measured,
            // not assumed. Both are set so that anything deriving a path from the base also lands inside
            // the isolated directory.
            startInfo.Environment["MSBuildProjectExtensionsPath"] = directory;
            startInfo.Environment["BaseIntermediateOutputPath"] = directory;
            startInfo.Environment["ProjectAssetsFile"] = Path.Combine(
                directory,
                "project.assets.json"
            );
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            return (string.Empty, "The 'dotnet' CLI was not found in PATH. Ensure the .NET SDK is installed and 'dotnet' is accessible.", false);
        }

        if (process == null)
        {
            return (string.Empty, string.Empty, false);
        }

        using (process)
        {
            // Read streams before WaitForExitAsync to prevent deadlock when output buffers fill
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var output = await outputTask;
            var error = await errorTask;
            await process.WaitForExitAsync();

            return (output, error, process.ExitCode == 0);
        }
    }
}
