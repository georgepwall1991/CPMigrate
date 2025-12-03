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
    Json
}
