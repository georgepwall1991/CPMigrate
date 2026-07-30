using Spectre.Console;
using Spectre.Console.Rendering;

namespace CPMigrate.Services;

/// <summary>
/// Renders a <see cref="FigletText"/> with a horizontal colour gradient, so the banner
/// transitions from one palette colour to another across its width instead of being a
/// single flat colour.
/// </summary>
internal sealed class GradientFigletText : IRenderable
{
    private readonly string _text;
    private readonly Color _start;
    private readonly Color _end;

    public GradientFigletText(string text, Color start, Color end)
    {
        _text = text;
        _start = start;
        _end = end;
    }

    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        var lines = GetFigletLines();
        var longest = lines.Count > 0 ? lines.Max(l => l.Length) : 0;
        return new Measurement(Math.Min(longest, maxWidth), Math.Min(longest, maxWidth));
    }

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = GetFigletLines();
        var totalWidth = lines.Count > 0 ? lines.Max(l => l.TrimEnd().Length) : 0;

        if (totalWidth == 0)
        {
            yield return Segment.Empty;
            yield break;
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            for (var i = 0; i < trimmed.Length; i++)
            {
                var fraction = totalWidth > 1 ? (double)i / (totalWidth - 1) : 0;
                var color = Lerp(_start, _end, fraction);
                yield return new Segment(trimmed[i].ToString(), new Style(color));
            }

            yield return Segment.LineBreak;
        }
    }

    private List<string> GetFigletLines()
    {
        var buffer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(buffer),
            Interactive = InteractionSupport.No,
            Ansi = AnsiSupport.No,
        });

        console.Write(new FigletText(_text).LeftJustified());
        return buffer.ToString().Split('\n').ToList();
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        var r = (byte)(a.R + (b.R - a.R) * t);
        var g = (byte)(a.G + (b.G - a.G) * t);
        var bl = (byte)(a.B + (b.B - a.B) * t);
        return new Color(r, g, bl);
    }
}
