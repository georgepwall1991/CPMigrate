using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for JsonFormatter covering JSON serialization of results.
/// </summary>
public class JsonFormatterTests
{
    [Fact]
    public void Format_OperationResult_ReturnsValidJson()
    {
        // Arrange
        var formatter = new JsonFormatter();
        var result = new OperationResult
        {
            Operation = "Test operation",
            ExitCode = ExitCodes.Success,
            Success = true
        };

        // Act
        var json = formatter.Format(result);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("\"exitCode\": 0");
        json.Should().Contain("\"operation\": \"Test operation\"");
    }

    [Fact]
    public void Format_OperationResult_UsesCamelCaseNaming()
    {
        // Arrange
        var formatter = new JsonFormatter();
        var result = new OperationResult { ExitCode = ExitCodes.Success };

        // Act
        var json = formatter.Format(result);

        // Assert
        json.Should().Contain("exitCode"); // camelCase, not ExitCode
        json.Should().NotContain("ExitCode"); // PascalCase
    }

    [Fact]
    public void Format_OperationResult_IsIndented()
    {
        // Arrange
        var formatter = new JsonFormatter();
        var result = new OperationResult { ExitCode = ExitCodes.Success };

        // Act
        var json = formatter.Format(result);

        // Assert
        json.Should().Contain("\n"); // Contains newlines (indented)
        json.Should().Contain("  "); // Contains spaces (indentation)
    }

    [Fact]
    public void Format_BatchResult_ReturnsValidJson()
    {
        // Arrange
        var formatter = new JsonFormatter();
        var batchResult = new BatchResult
        {
            Solutions = new List<SolutionResult>
            {
                new() { Path = "Solution1.sln", Success = true, ExitCode = ExitCodes.Success }
            }
        };

        // Act
        var json = formatter.Format(batchResult);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("solutions");
        json.Should().Contain("Solution1.sln");
    }

    [Fact]
    public void Format_BatchResult_WithErrors_IncludesErrors()
    {
        // Arrange
        var formatter = new JsonFormatter();
        var batchResult = new BatchResult
        {
            Errors = new List<string> { "Error 1", "Error 2" }
        };

        // Act
        var json = formatter.Format(batchResult);

        // Assert
        json.Should().Contain("errors");
        json.Should().Contain("Error 1");
        json.Should().Contain("Error 2");
    }

    [Fact]
    public void IOutputFormatter_Format_OperationResult_WritesToOutput()
    {
        // Arrange
        using var writer = new StringWriter();
        var formatter = new JsonFormatter(writer);
        var result = new OperationResult { ExitCode = ExitCodes.Success };

        // Act
        ((IOutputFormatter)formatter).Format(result);

        // Assert
        var output = writer.ToString();
        output.Should().NotBeNullOrEmpty();
        output.Should().Contain("exitCode");
    }

    [Fact]
    public void IOutputFormatter_Format_BatchResult_WritesToOutput()
    {
        // Arrange
        using var writer = new StringWriter();
        var formatter = new JsonFormatter(writer);
        var batchResult = new BatchResult
        {
            Solutions = new List<SolutionResult>
            {
                new() { Path = "Solution1.sln" }
            }
        };

        // Act
        ((IOutputFormatter)formatter).Format(batchResult);

        // Assert
        var output = writer.ToString();
        output.Should().NotBeNullOrEmpty();
        output.Should().Contain("solutions");
    }

    [Fact]
    public void Constructor_NoOutput_UsesConsoleOut()
    {
        // Arrange & Act
        var formatter = new JsonFormatter();

        // Assert - should not throw and should be usable
        var result = new OperationResult { ExitCode = ExitCodes.Success };
        var json = formatter.Format(result);
        json.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Constructor_CustomOutput_UsesProvidedWriter()
    {
        // Arrange
        using var writer = new StringWriter();

        // Act
        var formatter = new JsonFormatter(writer);
        var result = new OperationResult { ExitCode = ExitCodes.Success };
        ((IOutputFormatter)formatter).Format(result);

        // Assert
        writer.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Format_SerializedJson_CanBeDeserialized()
    {
        // Arrange
        var formatter = new JsonFormatter();
        var originalResult = new OperationResult
        {
            Operation = "Test operation",
            ExitCode = ExitCodes.ValidationError
        };

        // Act
        var json = formatter.Format(originalResult);
        var deserializedResult = JsonSerializer.Deserialize<OperationResult>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Assert
        deserializedResult.Should().NotBeNull();
        deserializedResult!.Operation.Should().Be("Test operation");
        deserializedResult.ExitCode.Should().Be(ExitCodes.ValidationError);
    }
}
