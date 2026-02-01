using System.ComponentModel;
using System.Diagnostics;

namespace CPMigrate.Services;

/// <summary>
/// Implementation of .NET CLI interactions.
/// </summary>
public class DotNetCliService : IDotNetCliService
{
    public async Task<(string Output, bool Success)> RunListPackageAsync(string projectDir, bool includeTransitive, bool vulnerable)
    {
        var args = "list package";
        if (vulnerable)
        {
            args += " --vulnerable";
        }

        if (includeTransitive)
        {
            args += " --include-transitive";
        }

        var startInfo = new ProcessStartInfo
        {
#pragma warning disable S4036 // Suppress PATH warning: CLI tool intentionally uses dotnet from PATH
            FileName = "dotnet",
#pragma warning restore S4036
            Arguments = args,
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            return ("The 'dotnet' CLI was not found in PATH. Ensure the .NET SDK is installed and 'dotnet' is accessible.", false);
        }

        if (process == null)
        {
            return (string.Empty, false);
        }

        using (process)
        {
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (output, process.ExitCode == 0);
        }
    }
}
