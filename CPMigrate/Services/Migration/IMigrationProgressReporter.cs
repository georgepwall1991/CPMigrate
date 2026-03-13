using Spectre.Console;

namespace CPMigrate.Services.Migration;

internal interface IMigrationProgressReporter
{
    Task<T> RunStatusAsync<T>(string description, Func<Task<T>> action);
    Task RunProgressAsync(string description, int total, Func<IMigrationProgressContext, Task> action);
}

internal interface IMigrationProgressContext
{
    void SetDescription(string description);
    void Increment(double value = 1);
    ProgressTask? Task { get; }
}

