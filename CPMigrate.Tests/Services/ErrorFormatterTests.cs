using CPMigrate.Services;
using FluentAssertions;
using Spectre.Console.Testing;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Error output is the whole support interaction for a headless CI run: the title has to name
/// the failure class, the detail has to carry the facts, and the suggestion has to say what
/// to do next. These pins keep each failure class distinct and keep user input that looks
/// like Spectre markup from breaking the render.
/// </summary>
public class ErrorFormatterTests
{
    [Fact]
    public void RenderValidationError_NamesTheFailureAndPointsAtHelp()
    {
        var console = new TestConsole().Interactive();

        ErrorFormatter.RenderValidationError(console, GlyphSet.Unicode, "Missing --solution.");

        console.Output.Should().Contain("Invalid arguments");
        console.Output.Should().Contain("Missing --solution.");
        console.Output.Should().Contain("--help");
    }

    [Fact]
    public void RenderFileError_NamesTheFailureAndMentionsLocks()
    {
        var console = new TestConsole().Interactive();

        ErrorFormatter.RenderFileError(console, GlyphSet.Unicode, "Could not write props.");

        console.Output.Should().Contain("File operation failed");
        console.Output.Should().Contain("Could not write props.");
        console.Output.Should().Contain("locked");
    }

    [Fact]
    public void RenderPermissionError_NamesTheFailureAndMentionsAccess()
    {
        var console = new TestConsole().Interactive();

        ErrorFormatter.RenderPermissionError(console, GlyphSet.Unicode, "Access denied.");

        console.Output.Should().Contain("Permission denied");
        console.Output.Should().Contain("Access denied.");
    }

    [Fact]
    public void RenderUnexpectedError_PointsAtTheIssueTrackerAndDocs()
    {
        var console = new TestConsole().Interactive();

        ErrorFormatter.RenderUnexpectedError(console, GlyphSet.Unicode, "Object reference.");

        console.Output.Should().Contain("Unexpected error");
        console.Output.Should().Contain("Object reference.");
        console.Output.Should().Contain("github.com/georgepwall1991/CPMigrate/issues");
        console.Output.Should().Contain("Docs:");
    }

    [Fact]
    public void Render_WithoutSuggestionOrDocs_OmitsBothRows()
    {
        var console = new TestConsole().Interactive();

        ErrorFormatter.Render(console, GlyphSet.Unicode, "Title", "Detail");

        console.Output.Should().Contain("Title");
        console.Output.Should().Contain("Detail");
        console.Output.Should().NotContain("Docs:");
    }

    [Fact]
    public void Render_MarkupInDetail_RendersLiterallyInsteadOfBreaking()
    {
        // Paths and messages routinely contain brackets; unescaped they parse as Spectre
        // markup and either throw or swallow the text.
        var console = new TestConsole().Interactive();

        ErrorFormatter.RenderValidationError(console, GlyphSet.Unicode, "Bad value [foo] in C:\\proj\\[net8].props.");

        console.Output.Should().Contain("[foo]");
        console.Output.Should().Contain("[net8]");
    }

    [Fact]
    public void Render_AsciiGlyphs_RendersTheSameContent()
    {
        var console = new TestConsole().Interactive();

        ErrorFormatter.RenderUnexpectedError(console, GlyphSet.Ascii, "Boom.");

        console.Output.Should().Contain("Unexpected error");
        console.Output.Should().Contain("Boom.");
    }
}
