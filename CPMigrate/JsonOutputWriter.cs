using CPMigrate.Services;

namespace CPMigrate;

/// <summary>
/// Centralizes JSON output emission: serializes operation results and writes them
/// either to a file (when <see cref="Options.OutputFile"/> is set) or directly to stdout.
/// Stdout writes bypass <see cref="IConsoleService"/> intentionally so raw machine-readable
/// JSON is emitted verbatim, even under <c>--quiet</c> or the silent console used in JSON mode.
/// </summary>
internal static class JsonOutputWriter
{
    /// <summary>
    /// Emits serialized JSON to the configured destination: a file when <paramref name="options.OutputFile"/>
    /// is set (with an optional confirmation message), otherwise stdout.
    /// </summary>
    /// <param name="json">The serialized JSON payload to emit.</param>
    /// <param name="options">The parsed options controlling output destination and quiet behavior.</param>
    /// <param name="consoleService">The console service used to announce file writes (suppressed under <c>--quiet</c>).</param>
    /// <param name="announceFile">When <c>true</c>, writes the "… output written to: …" notice after a file write (ignored under <c>--quiet</c> or for stdout output). Defaults to <c>true</c>.</param>
    public static async Task EmitAsync(
        string json,
        Options options,
        IConsoleService? consoleService,
        bool announceFile = true
    )
    {
        if (!string.IsNullOrEmpty(options.OutputFile))
        {
            await File.WriteAllTextAsync(options.OutputFile, json);
            if (announceFile && !options.Quiet && consoleService is not null)
            {
                var format = options.Output == OutputFormat.Sarif ? "SARIF" : "JSON";
                consoleService.Dim($"{format} output written to: {options.OutputFile}");
            }
            return;
        }

        Console.WriteLine(json);
    }
}
