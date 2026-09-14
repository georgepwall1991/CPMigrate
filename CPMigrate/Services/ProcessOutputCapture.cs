using System.Diagnostics;
using System.Text;

namespace CPMigrate.Services;

/// <summary>
/// Drains a child process's redirected stdout/stderr without ever hanging on the pipes.
/// </summary>
/// <remarks>
/// Waiting for stream EOF is not the same thing as the process exiting: a detached grandchild —
/// an MSBuild <c>/nodeReuse:true</c> process is the canonical offender — inherits the pipe handles
/// and can hold them open for minutes after the child is gone. <c>ReadToEnd</c>,
/// <c>ReadToEndAsync</c>, and the parameterless <see cref="Process.WaitForExit()"/> all block on
/// that EOF and hang forever even though the process is dead (dotnet/msbuild#2981, #10530).
///
/// Line callbacks keep the pipes drained so a chatty child can never fill the buffer and block,
/// the exit wait is on the process handle only (the Exited event — every Process.WaitForExit
/// flavour that observes EOF can hang on a held-open pipe), and the EOF wait afterwards is
/// bounded — by the time the child exits, everything it wrote has already been delivered.
/// </remarks>
internal sealed class ProcessOutputCapture : IDisposable
{
    private static readonly TimeSpan DefaultEndOfStreamTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _endOfStreamTimeout;
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _error = new();
    private readonly ManualResetEventSlim _outputEof = new();
    private readonly ManualResetEventSlim _errorEof = new();

    public ProcessOutputCapture(TimeSpan? endOfStreamTimeout = null)
    {
        _endOfStreamTimeout = endOfStreamTimeout ?? DefaultEndOfStreamTimeout;
    }

    /// <summary>Everything received on stdout so far, one line per <c>AppendLine</c>.</summary>
    public string Output => _output.ToString();

    /// <summary>Everything received on stderr so far.</summary>
    public string Error => _error.ToString();

    /// <summary>
    /// Attaches the drain callbacks and starts the async reads. Call immediately after
    /// <see cref="Process.Start()"/>; only streams the caller redirected are drained.
    /// </summary>
    public void BeginCapture(Process process)
    {
        if (process.StartInfo.RedirectStandardOutput)
        {
            process.OutputDataReceived += (_, args) => OnLine(_output, _outputEof, args);
            process.BeginOutputReadLine();
        }

        if (process.StartInfo.RedirectStandardError)
        {
            process.ErrorDataReceived += (_, args) => OnLine(_error, _errorEof, args);
            process.BeginErrorReadLine();
        }
    }

    /// <summary>
    /// Waits for the process handle — not for pipe EOF — then bounds the drain.
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/> cannot be used here: once async
    /// stream readers are attached it also awaits their EOF, which a detached grandchild never
    /// delivers. The Exited event signals on the handle alone.
    /// </summary>
    public async Task WaitAsync(Process process, CancellationToken cancellationToken = default)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult();
        if (process.HasExited)
        {
            exited.TrySetResult();
        }

        await exited.Task.WaitAsync(cancellationToken);
        WaitForEndOfStream();
    }

    /// <summary>
    /// Sync variant for call sites that cannot go async. <see cref="Process.WaitForExit(int)"/>
    /// with a finite-looking timeout is the documented way to wait on the handle without the
    /// runtime then blocking on pipe EOF behind the caller's back (dotnet/msbuild#2981).
    /// </summary>
    public void Wait(Process process)
    {
        process.WaitForExit(int.MaxValue);
        WaitForEndOfStream();
    }

    /// <summary>
    /// Sync variant with an overall exit timeout. Returns false when the process did not exit in
    /// time; the caller is responsible for killing it.
    /// </summary>
    public bool Wait(Process process, TimeSpan exitTimeout)
    {
        if (!process.WaitForExit(exitTimeout))
        {
            return false;
        }

        WaitForEndOfStream();
        return true;
    }

    private void WaitForEndOfStream()
    {
        _outputEof.Wait(_endOfStreamTimeout);
        _errorEof.Wait(_endOfStreamTimeout);
    }

    private static void OnLine(StringBuilder target, ManualResetEventSlim eof, DataReceivedEventArgs args)
    {
        if (args.Data is null)
        {
            eof.Set();
            return;
        }

        target.AppendLine(args.Data);
    }

    public void Dispose()
    {
        _outputEof.Dispose();
        _errorEof.Dispose();
    }
}
