using Spectre.Console;
using Spectre.Console.Rendering;

namespace CPMigrate.Services;

/// <summary>
/// Shared cyberpunk color palette and markup helpers for the Spectre.Console renderers.
/// </summary>
internal static class SpectrePalette
{
    public static class CyberColors
    {
        public static readonly Color Primary = Color.DeepPink1;
        public static readonly Color Secondary = Color.Cyan1;
        public static readonly Color Success = Color.SpringGreen1;
        public static readonly Color Warning = Color.Orange1;
        public static readonly Color Error = Color.Red1;
        public static readonly Color Dim = Color.Grey39;
        public static readonly Color Accent = Color.Yellow1;
    }

    /// <summary>
    /// The same palette expressed as markup names, so renderers interpolating into
    /// markup strings never hard-code a colour literal that can drift from
    /// <see cref="CyberColors"/>.
    /// </summary>
    public static class Ink
    {
        public const string Primary = "deeppink1";
        public const string Secondary = "cyan1";
        public const string Success = "springgreen1";
        public const string Warning = "orange1";
        public const string Error = "red1";
        public const string Dim = "grey39";
        public const string Accent = "yellow1";
        public const string Text = "white";
        public const string Muted = "grey";
    }

    public static string Escape(string text) => Markup.Escape(text);

    /// <summary>
    /// Renders a horizontal bar meter as markup — filled cells in <paramref name="color"/>,
    /// the remainder dimmed. <paramref name="fraction"/> is clamped to 0..1.
    /// </summary>
    public static string Meter(double fraction, string color, GlyphSet glyphs, int width = 14)
    {
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return $"[{color}]{new string(glyphs.MeterFilled[0], filled)}[/]" +
               $"[{Ink.Dim}]{new string(glyphs.MeterEmpty[0], width - filled)}[/]";
    }

    /// <summary>
    /// Wraps a renderable in a rounded panel with a colored border and optional header.
    /// Centralizes the panel-construction pattern used across the dashboard renderers.
    /// </summary>
    public static Panel WrapRoundedPanel(IRenderable content, Color borderColor, PanelHeader? header = null, Padding? padding = null)
    {
        var panel = new Panel(content)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(borderColor),
            Padding = padding ?? new Padding(1, 0),
        };
        if (header != null)
        {
            panel.Header = header;
        }
        return panel;
    }
}
