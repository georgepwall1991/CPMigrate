using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Interface for formatting operation results to different output formats.
/// </summary>
public interface IOutputFormatter
{
    /// <summary>
    /// Format and output an operation result.
    /// </summary>
    /// <param name="result">The operation result to format.</param>
    void Format(OperationResult result);

    /// <summary>
    /// Format and output a batch result.
    /// </summary>
    /// <param name="result">The batch result to format.</param>
    void Format(BatchResult result);
}

/// <summary>
/// Output format options.
/// </summary>
public enum OutputFormat
{
    /// <summary>Rich terminal output with colors and formatting (default).</summary>
    Terminal,

    /// <summary>JSON output for CI/CD integration.</summary>
    Json,

    /// <summary>SARIF 2.1.0 output for GitHub code scanning and other static-analysis consumers.</summary>
    Sarif,
}

/// <summary>
/// Helpers for reasoning about output formats.
/// </summary>
public static class OutputFormatExtensions
{
    /// <summary>
    /// Returns true when the format produces a machine-readable document on stdout. Those formats
    /// must never be polluted by banners, progress bars, or prompts, so callers use this to decide
    /// whether to silence the console rather than testing for one specific format.
    /// </summary>
    /// <param name="format">The configured output format.</param>
    public static bool IsMachineReadable(this OutputFormat format)
    {
        return format is OutputFormat.Json or OutputFormat.Sarif;
    }
}
