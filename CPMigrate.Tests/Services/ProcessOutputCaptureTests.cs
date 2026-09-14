using System.Diagnostics;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// The failure these tests pin down: waiting on a redirected pipe's EOF is not the same as the
/// child exiting. A detached grandchild — an MSBuild <c>/nodeReuse:true</c> process is the one we
/// actually hit, hanging a --verify restore for twenty-plus minutes — inherits the handles and
/// holds them open after the child is gone, so <c>ReadToEnd</c> and parameterless
/// <c>WaitForExit()</c> never return (dotnet/msbuild#2981, #10530).
/// </summary>
public class ProcessOutputCaptureTests
{
    [Fact]
    public async Task WaitAsync_CapturesBothStreams()
    {
        using var process = Start(ShellOut("echo out; echo err 1>&2", "echo out & echo err 1>&2"));

        using var capture = new ProcessOutputCapture(TimeSpan.FromSeconds(5));
        capture.BeginCapture(process);
        await capture.WaitAsync(process);

        process.ExitCode.Should().Be(0);
        capture.Output.Should().Contain("out");
        capture.Error.Should().Contain("err");
    }

    [Fact]
    public void Wait_ReturnsPromptly_WhenADetachedGrandchildHoldsThePipeOpen()
    {
        // The child prints and exits immediately; the grandchild it spawned inherits the pipe and
        // sleeps on, so the write end never closes. Without a bounded EOF wait this hangs until the
        // sleeper dies — here long enough that finishing the test proves the bound worked.
        using var process = Start(ShellGrandchild());

        using var capture = new ProcessOutputCapture(TimeSpan.FromMilliseconds(250));
        capture.BeginCapture(process);
        capture.Wait(process);

        process.HasExited.Should().BeTrue("the child is long gone — only the pipe was held");
        capture.Output.Should().Contain("done", "output delivered before the child exited is not lost");
    }

    [Fact]
    public async Task WaitAsync_ReturnsPromptly_WhenADetachedGrandchildHoldsThePipeOpen()
    {
        using var process = Start(ShellGrandchild());

        using var capture = new ProcessOutputCapture(TimeSpan.FromMilliseconds(250));
        capture.BeginCapture(process);
        await capture.WaitAsync(process);

        capture.Output.Should().Contain("done");
    }

    [Fact]
    public void Wait_WithExitTimeout_ReturnsFalse_WhenTheProcessDoesNotExit()
    {
        using var process = Start(ShellOut("sleep 30", "ping -n 30 127.0.0.1 >nul"));

        using var capture = new ProcessOutputCapture(TimeSpan.FromMilliseconds(100));
        capture.BeginCapture(process);

        var exited = capture.Wait(process, TimeSpan.FromMilliseconds(250));

        exited.Should().BeFalse();
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // It managed to exit between the timeout and the kill — the assertion above still stands.
        }
    }

    [Fact]
    public void BeginCapture_OnlyDrainsRedirectedStreams()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo hi" : "-c \"echo hi\"",
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        process.Start();

        using var capture = new ProcessOutputCapture(TimeSpan.FromMilliseconds(500));
        capture.BeginCapture(process);
        capture.Wait(process);

        capture.Output.Should().Contain("hi");
        capture.Error.Should().BeEmpty("stderr was never redirected — nothing to drain");
    }

    /// <summary>
    /// A command whose child exits while a detached grandchild keeps the pipe open. The grandchild
    /// outlives the test either way — keep it short so nothing lingers.
    /// </summary>
    private static (string FileName, string Arguments) ShellGrandchild() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c \"echo done & start /b ping -n 6 127.0.0.1\"")
            : ("/bin/sh", "-c \"sleep 5 & echo done\"");

    private static (string FileName, string Arguments) ShellOut(string unix, string windows) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c \"{windows}\"")
            : ("/bin/sh", $"-c \"{unix}\"");

    private static Process Start((string FileName, string Arguments) command)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.Start();
        return process;
    }
}
