using CPMigrate.Services.Migration;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Migration;

public class MigrationProgressReporterTests
{
    [Fact]
    public async Task RunStatusAsync_QuietMode_ExecutesActionDirectly()
    {
        var reporter = new MigrationProgressReporter(quietMode: true);

        var result = await reporter.RunStatusAsync("quiet", () => Task.FromResult(42));

        result.Should().Be(42);
    }

    [Fact]
    public async Task RunProgressAsync_QuietMode_UsesQuietContext()
    {
        var reporter = new MigrationProgressReporter(quietMode: true);
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
    public async Task RunStatusAsync_InteractiveMode_ExecutesAction()
    {
        var reporter = new MigrationProgressReporter(quietMode: false);
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
    public async Task RunProgressAsync_InteractiveMode_ProvidesWritableContext()
    {
        var reporter = new MigrationProgressReporter(quietMode: false);
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
}
