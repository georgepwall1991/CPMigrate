using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Renders structured, actionable error output so users know what happened,
/// why, and what to do next — instead of a bare message they have to decode.
/// </summary>
internal static class ErrorFormatter
{
    public static void Render(IAnsiConsole console, GlyphSet glyphs, string title, string detail, string? suggestion = null, string? docsUrl = null)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Padding(0, 0, 1, 0));
        grid.AddColumn(new GridColumn());

        grid.AddRow(
            $"[{SpectrePalette.Ink.Error}]{glyphs.Error}[/]",
            $"[bold {SpectrePalette.Ink.Error}]{Markup.Escape(title)}[/]");

        grid.AddRow(
            string.Empty,
            $"[{SpectrePalette.Ink.Text}]{Markup.Escape(detail)}[/]");

        if (suggestion is not null)
        {
            grid.AddRow(
                $"[{SpectrePalette.Ink.Secondary}]{glyphs.Arrow}[/]",
                $"[{SpectrePalette.Ink.Secondary}]{Markup.Escape(suggestion)}[/]");
        }

        if (docsUrl is not null)
        {
            grid.AddRow(
                $"[{SpectrePalette.Ink.Dim}]{glyphs.Info}[/]",
                $"[{SpectrePalette.Ink.Dim}]Docs: {Markup.Escape(docsUrl)}[/]");
        }

        var panel = new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(SpectrePalette.CyberColors.Error),
            Padding = new Padding(1, 0),
        };

        console.WriteLine();
        console.Write(panel);
        console.WriteLine();
    }

    public static void RenderValidationError(IAnsiConsole console, GlyphSet glyphs, string detail)
    {
        Render(console, glyphs,
            "Invalid arguments",
            detail,
            "Run 'cpmigrate --help' to see every available flag.");
    }

    public static void RenderFileError(IAnsiConsole console, GlyphSet glyphs, string detail)
    {
        Render(console, glyphs,
            "File operation failed",
            detail,
            "Check file permissions and ensure no files are locked by another process.");
    }

    public static void RenderPermissionError(IAnsiConsole console, GlyphSet glyphs, string detail)
    {
        Render(console, glyphs,
            "Permission denied",
            detail,
            "Run with elevated permissions or check file/folder access rights.");
    }

    public static void RenderUnexpectedError(IAnsiConsole console, GlyphSet glyphs, string detail)
    {
#pragma warning disable S1075 // URIs should not be hardcoded - the issue tracker and docs are fixed locations
        Render(console, glyphs,
            "Unexpected error",
            detail,
            "Report this at https://github.com/georgepwall1991/CPMigrate/issues",
            "https://georgepwall1991.github.io/CPMigrate/");
#pragma warning restore S1075
    }
}
