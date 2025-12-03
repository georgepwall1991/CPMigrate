using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Formats operation results as JSON for CI/CD integration.
/// </summary>
public class JsonFormatter : IOutputFormatter
{
    private readonly TextWriter _output;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new JSON formatter writing to the specified output.
    /// </summary>
    /// <param name="output">The output stream to write to. Defaults to stdout.</param>
    public JsonFormatter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <inheritdoc />
    void IOutputFormatter.Format(OperationResult result)
    {
        var json = JsonSerializer.Serialize(result, _jsonOptions);
        _output.WriteLine(json);
    }

    /// <inheritdoc />
    void IOutputFormatter.Format(BatchResult result)
    {
        var json = JsonSerializer.Serialize(result, _jsonOptions);
        _output.WriteLine(json);
    }

    /// <summary>
    /// Formats an operation result as JSON string.
    /// </summary>
    /// <param name="result">The result to format.</param>
    /// <returns>JSON string representation.</returns>
    public string Format(OperationResult result)
    {
        return JsonSerializer.Serialize(result, _jsonOptions);
    }

    /// <summary>
    /// Formats a batch result as JSON string.
    /// </summary>
    /// <param name="result">The result to format.</param>
    /// <returns>JSON string representation.</returns>
    public string Format(BatchResult result)
    {
        return JsonSerializer.Serialize(result, _jsonOptions);
    }
}
