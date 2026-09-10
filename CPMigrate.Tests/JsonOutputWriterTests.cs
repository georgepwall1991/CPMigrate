using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The <c>--output-file</c> contract: the file's parent is part of the path the caller named, so
/// a CI script writing to <c>artifacts/report.json</c> should not have to <c>mkdir -p</c> first.
/// </summary>
public class JsonOutputWriterTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"CPMigrateJsonWriter_{Guid.NewGuid():N}"
    );

    public JsonOutputWriterTests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EmitAsync_CreatesTheParentDirectory()
    {
        var outputFile = Path.Combine(_testDirectory, "reports", "out", "result.json");
        var options = new Options { Output = OutputFormat.Json, OutputFile = outputFile };

        await JsonOutputWriter.EmitAsync("{}", options, null);

        File.Exists(outputFile).Should().BeTrue();
        (await File.ReadAllTextAsync(outputFile)).Should().Be("{}");
    }

    [Fact]
    public async Task EmitAsync_ExistingParentDirectory_StillWrites()
    {
        var outputFile = Path.Combine(_testDirectory, "result.json");
        var options = new Options { Output = OutputFormat.Json, OutputFile = outputFile };

        await JsonOutputWriter.EmitAsync("{\"a\":1}", options, null);

        (await File.ReadAllTextAsync(outputFile)).Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task EmitFailureAsync_UnwritableFile_FallsBackToStdout()
    {
        // The error handler is often reached because the output path is bad; retrying it there
        // would throw a second time out of a catch block and abort the process.
        var outputFile = Path.Combine(_testDirectory, "no-such-dir", "..", ".");
        var options = new Options { Output = OutputFormat.Json, OutputFile = outputFile };

        var original = Console.Out;
        using var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            await JsonOutputWriter.EmitFailureAsync("{\"error\":true}", options);
        }
        finally
        {
            Console.SetOut(original);
        }

        stdout.ToString().Should().Contain("{\"error\":true}");
    }
}
