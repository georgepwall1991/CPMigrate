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
                var format = options.Output switch
                {
                    OutputFormat.Sarif => "SARIF",
                    OutputFormat.Markdown => "Markdown",
                    _ => "JSON",
                };
                consoleService.Dim($"{format} output written to: {options.OutputFile}");
            }
            return;
        }

        Console.WriteLine(json);
    }

    /// <summary>
    /// Emits a payload that reports a failure, falling back to stdout when the configured output
    /// file cannot be written. The error handler is often reached <em>because</em> the output path
    /// is bad; retrying it there would throw a second time out of a catch block and abort the
    /// process instead of reporting the original problem.
    /// </summary>
    /// <param name="json">The serialized failure payload.</param>
    /// <param name="options">The parsed options controlling output destination.</param>
    public static async Task EmitFailureAsync(string json, Options options)
    {
        try
        {
            await EmitAsync(json, options, null, announceFile: false);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine(json);
        }
    }
}
