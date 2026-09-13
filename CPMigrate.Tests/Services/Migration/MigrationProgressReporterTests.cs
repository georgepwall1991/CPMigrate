using CPMigrate.Services.Migration;
using FluentAssertions;
using Spectre.Console.Testing;

namespace CPMigrate.Tests.Services.Migration;

public class MigrationProgressReporterTests
{
    [Fact]
    public async Task RunStatusAsync_QuietMode_ExecutesActionDirectly()
    {
        var reporter = new MigrationProgressReporter(quietMode: true, liveConsole: new TestConsole());

        var result = await reporter.RunStatusAsync("quiet", () => Task.FromResult(42));

        result.Should().Be(42);
    }

    [Fact]
    public async Task RunProgressAsync_QuietMode_UsesQuietContext()
    {
        var reporter = new MigrationProgressReporter(quietMode: true, liveConsole: new TestConsole());
        IMigrationProgressContext? captured = null;

        await reporter.RunProgressAsync("quiet progress", 3, context =>
        {
            captured = context;
            context.Task.Should().BeNull();
            context.SetDescription("noop");
            context.Increment();
            return Task.CompletedTask;
        });

        captured.Should().NotBeNull();
    }

    [Fact]
    public async Task RunStatusAsync_NoLiveConsole_ExecutesActionDirectly()
    {
        // No live surface means no widget: a null console is the answer silent, buffered, and
        // redirected consoles all give, and the action must still run plainly.
        var reporter = new MigrationProgressReporter(quietMode: false, liveConsole: null);

        var result = await reporter.RunStatusAsync("no console", () => Task.FromResult(42));

        result.Should().Be(42);
    }

    [Fact]
    public async Task RunProgressAsync_NoLiveConsole_UsesQuietContext()
    {
        var reporter = new MigrationProgressReporter(quietMode: false, liveConsole: null);
        IMigrationProgressContext? captured = null;

        await reporter.RunProgressAsync("no console progress", 2, context =>
        {
            captured = context;
            context.Task.Should().BeNull();
            return Task.CompletedTask;
        });

        captured.Should().NotBeNull();
    }

    [Fact]
    public async Task RunStatusAsync_WithLiveConsole_ExecutesAction()
    {
        var reporter = new MigrationProgressReporter(quietMode: false, liveConsole: new TestConsole());
        var executed = false;

        var result = await reporter.RunStatusAsync("interactive", () =>
        {
            executed = true;
            return Task.FromResult("done");
        });

        executed.Should().BeTrue();
        result.Should().Be("done");
    }

    [Fact]
    public async Task RunProgressAsync_WithLiveConsole_ProvidesWritableContext()
    {
        var reporter = new MigrationProgressReporter(quietMode: false, liveConsole: new TestConsole());
        IMigrationProgressContext? captured = null;

        await reporter.RunProgressAsync("interactive progress", 2, context =>
        {
            captured = context;
            context.Task.Should().NotBeNull();
            context.SetDescription("updated");
            context.Increment();
            return Task.CompletedTask;
        });

        captured.Should().NotBeNull();
        captured!.Task!.Description.Should().Be("updated");
        captured.Task.Value.Should().Be(1);
    }

    [Fact]
    public async Task RunProgressAsync_WithLiveConsole_DrawsOnThatConsole()
    {
        // The whole point of the parameter: widget frames go to the injected console, so a test
        // can read them back — and the process-wide console never sees a refresh thread's write.
        // Spectre only renders a Progress on an interactive profile, so the test console opts in.
        var live = new TestConsole().Interactive();
        var reporter = new MigrationProgressReporter(quietMode: false, liveConsole: live);

        await reporter.RunProgressAsync("scanning projects", 1, context =>
        {
            context.Increment();
            return Task.CompletedTask;
        });

        live.Output.Should().Contain("scanning projects");
    }
}
