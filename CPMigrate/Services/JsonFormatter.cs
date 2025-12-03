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
    public void Format(OperationResult result)
    {
        var json = JsonSerializer.Serialize(result, _jsonOptions);
        _output.WriteLine(json);
    }

    /// <inheritdoc />
    public void Format(BatchResult result)
    {
        var json = JsonSerializer.Serialize(result, _jsonOptions);
        _output.WriteLine(json);
    }
}
