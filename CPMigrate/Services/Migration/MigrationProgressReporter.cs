using Spectre.Console;

namespace CPMigrate.Services.Migration;

internal sealed class MigrationProgressReporter : IMigrationProgressReporter
{
    private readonly bool _quietMode;

    public MigrationProgressReporter(bool quietMode)
    {
        _quietMode = quietMode;
    }

    public async Task<T> RunStatusAsync<T>(string description, Func<Task<T>> action)
    {
        if (_quietMode)
        {
            return await action();
        }

        return await AnsiConsole.Status()
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
        if (_quietMode)
        {
            await action(QuietProgressContext.Instance);
            return;
        }

        await AnsiConsole.Progress()
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

