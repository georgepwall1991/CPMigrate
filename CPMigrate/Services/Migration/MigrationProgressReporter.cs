using Spectre.Console;

namespace CPMigrate.Services.Migration;

internal sealed class MigrationProgressReporter : IMigrationProgressReporter
{
    private readonly bool _quietMode;
    private readonly IAnsiConsole? _liveConsole;

    /// <param name="quietMode">Suppresses widgets outright.</param>
    /// <param name="liveConsole">
    /// The surface a widget may draw on — the injected console's live surface, or null when the
    /// run has none. Drawn on the static <c>AnsiConsole.Console</c> instead, a widget writes to
    /// whatever stdout the process started with: under a test host that is a capture writer the
    /// refresh thread can still be ticking at disposal, and under a pipe it is a stream progress
    /// frames should never reach.
    /// </param>
    public MigrationProgressReporter(bool quietMode, IAnsiConsole? liveConsole)
    {
        _quietMode = quietMode;
        _liveConsole = liveConsole;
    }

    public async Task<T> RunStatusAsync<T>(string description, Func<Task<T>> action)
    {
        if (_quietMode || _liveConsole is not { } live)
        {
            return await action();
        }

        return await new Status(live)
            .Spinner(Spinner.Known.Dots12)
            .SpinnerStyle(new Style(SpectrePalette.CyberColors.Secondary))
            .StartAsync(description, async _ =>
            {
                await Task.Delay(100);
                return await action();
            });
    }

    public async Task RunProgressAsync(string description, int total, Func<IMigrationProgressContext, Task> action)
    {
        if (_quietMode || _liveConsole is not { } live)
        {
            await action(QuietProgressContext.Instance);
            return;
        }

        await new Progress(live)
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn { CompletedStyle = new Style(SpectrePalette.CyberColors.Success), FinishedStyle = new Style(SpectrePalette.CyberColors.Secondary) },
                new PercentageColumn(),
                new ElapsedTimeColumn(),
                new SpinnerColumn(Spinner.Known.Dots12) { CompletedStyle = new Style(SpectrePalette.CyberColors.Success) })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[{SpectrePalette.Ink.Text}]{description}[/]", maxValue: total);
                await action(new SpectreProgressContext(task));
            });
    }

    private sealed class QuietProgressContext : IMigrationProgressContext
    {
        public static QuietProgressContext Instance { get; } = new();

        public ProgressTask? Task => null;

        public void Increment(double value = 1)
        {
        }

        public void SetDescription(string description)
        {
        }
    }

    private sealed class SpectreProgressContext : IMigrationProgressContext
    {
        public SpectreProgressContext(ProgressTask task)
        {
            Task = task;
        }

        public ProgressTask? Task { get; }

        public void Increment(double value = 1)
        {
            Task?.Increment(value);
        }

        public void SetDescription(string description)
        {
            if (Task != null)
            {
                Task.Description = description;
            }
        }
    }
}

