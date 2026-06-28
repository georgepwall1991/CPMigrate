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

    public static string Escape(string text) => Markup.Escape(text);

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