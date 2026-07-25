using Spectre.Console;

namespace CPMigrate.Services;

/// <summary>
/// Per-console rendering theme. The glyph set is chosen from the target console's
/// capabilities so output stays legible on terminals without Unicode support
/// (legacy Windows consoles, redirected CI logs) instead of emitting replacement
/// characters where the status icons should be.
/// </summary>
internal sealed class SpectreTheme
{
    private SpectreTheme(GlyphSet glyphs) => Glyphs = glyphs;

    public GlyphSet Glyphs { get; }

    public static SpectreTheme For(IAnsiConsole console) =>
        new(console.Profile.Capabilities.Unicode ? GlyphSet.Unicode : GlyphSet.Ascii);
}

/// <summary>
/// The icons used across every renderer, in a Unicode and an ASCII flavour.
/// Keep the two in sync: every member must render at the same visual weight
/// in both, otherwise tables jitter between terminals.
/// </summary>
internal sealed record GlyphSet(
    string Info,
    string Success,
    string Warning,
    string Error,
    string Highlight,
    string Pending,
    string Current,
    string Arrow,
    string Bullet,
    string MeterFilled,
    string MeterEmpty)
{
    public static readonly GlyphSet Unicode = new(
        Info: "›",
        Success: "✔",
        Warning: "!",
        Error: "✖",
        Highlight: "»",
        Pending: "○",
        Current: "▶",
        Arrow: "➜",
        Bullet: "•",
        MeterFilled: "█",
        MeterEmpty: "░");

    public static readonly GlyphSet Ascii = new(
        Info: ">",
        Success: "+",
        Warning: "!",
        Error: "x",
        Highlight: ">",
        Pending: "o",
        Current: ">",
        Arrow: "->",
        Bullet: "*",
        MeterFilled: "#",
        MeterEmpty: "-");
}
