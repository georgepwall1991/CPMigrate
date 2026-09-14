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

        // Sequential ReadToEnd calls deadlock when the child fills stderr while the caller is
        // blocked on stdout, and any ReadToEnd can hang forever when a detached grandchild
        // inherits the pipes — the capture drains continuously and bounds the EOF wait.
        using var capture = new ProcessOutputCapture();
        capture.BeginCapture(process);
        capture.Wait(process);

        return (process.ExitCode, capture.Output, capture.Error);
    }
}
