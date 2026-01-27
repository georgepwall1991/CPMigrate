using System.Diagnostics;

namespace CPMigrate.Services;

public interface IProcessRunner
{
    (int ExitCode, string Output, string Error) Run(ProcessStartInfo startInfo);
}

public class ProcessRunner : IProcessRunner
{
    public (int ExitCode, string Output, string Error) Run(ProcessStartInfo startInfo)
    {
        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        return (process.ExitCode, output, error);
    }
}
