using System.Text.RegularExpressions;

namespace CPMigrate.Tests;

/// <summary>
/// Guards the static site in <c>site/</c> against the failure mode that keeps recurring there:
/// markup that references styling which does not exist.
///
/// <para>
/// Nothing throws when a CSS custom property is undefined or a class has no rule — the browser
/// silently falls back, so the page ships looking subtly wrong and no build catches it. This has
/// bitten three times: <c>.card .cmd</c> and <c>.section</c>/<c>.page-intro</c> had no rule at all,
/// and an inline <c>var(--pink)</c> outlived the token it referenced.
/// </para>
/// </summary>
public sealed class SiteAssetIntegrityTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    private static string SiteRoot => Path.Combine(RepositoryRoot, "site");

    private static string Stylesheet =>
        File.ReadAllText(Path.Combine(SiteRoot, "assets", "styles.css"));

    private static IEnumerable<string> HtmlFiles =>
        Directory.EnumerateFiles(SiteRoot, "*.html", SearchOption.AllDirectories);

    /// <summary>
    /// Custom properties set by JavaScript at runtime rather than declared in the stylesheet.
    /// Each is read through <c>var(--x, fallback)</c> or on an element that declares it inline.
    /// </summary>
    private static readonly HashSet<string> RuntimeAssigned = new(StringComparer.Ordinal)
    {
        "--mx",
        "--my",
        "--off",
        "--w",
        "--split",
    };

    private static HashSet<string> DeclaredCustomProperties()
    {
        var declared = new HashSet<string>(
            Regex
                .Matches(Stylesheet, @"^\s*(?<name>--[\w-]+)\s*:", RegexOptions.Multiline)
                .Select(m => m.Groups["name"].Value),
            StringComparer.Ordinal
        );

        foreach (var runtime in RuntimeAssigned)
        {
            declared.Add(runtime);
        }

        return declared;
    }

    [Fact]
    public void Stylesheet_never_reads_a_custom_property_it_does_not_declare()
    {
        var declared = DeclaredCustomProperties();

        var undefined = Regex
            .Matches(Stylesheet, @"var\((?<name>--[\w-]+)")
            .Select(m => m.Groups["name"].Value)
            .Where(name => !declared.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undefined.Count == 0,
            $"styles.css reads undeclared custom properties: {string.Join(", ", undefined)}"
        );
    }

    /// <summary>
    /// Inline <c>style="color:var(--x)"</c> in the markup is the easiest reference to miss when a
    /// token is renamed, because sweeping the stylesheet alone will not surface it.
    /// </summary>
    [Fact]
    public void Markup_never_reads_a_custom_property_the_stylesheet_does_not_declare()
    {
        var declared = DeclaredCustomProperties();
        var offenders = new List<string>();

        foreach (var file in HtmlFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in Regex.Matches(lines[i], @"var\((?<name>--[\w-]+)"))
                {
                    var name = match.Groups["name"].Value;
                    if (!declared.Contains(name))
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(RepositoryRoot, file)}:{i + 1} → {name}"
                        );
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Markup references custom properties styles.css does not declare:{Environment.NewLine}"
                + string.Join(Environment.NewLine, offenders)
        );
    }

    /// <summary>
    /// A class in the markup with no rule anywhere renders as unstyled text. Only classes the site
    /// actually styles are checked — utility hooks used solely by JavaScript are excluded by
    /// requiring the class to appear in a <c>class="..."</c> attribute of a real element.
    /// </summary>
    [Fact]
    public void Every_class_used_in_markup_has_a_rule_in_the_stylesheet()
    {
        var stylesheet = Stylesheet;
        var styled = new HashSet<string>(
            Regex
                .Matches(stylesheet, @"\.(?<name>[A-Za-z][\w-]*)")
                .Select(m => m.Groups["name"].Value),
            StringComparer.Ordinal
        );

        // Applied by script at runtime, so they never appear in a static class attribute.
        string[] scriptApplied = ["in", "on", "open", "show", "ok", "sel"];

        var unstyled = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in HtmlFiles)
        {
            var html = File.ReadAllText(file);
            foreach (Match attribute in Regex.Matches(html, @"class=""(?<value>[^""]+)"""))
            {
                foreach (
                    var name in attribute
                        .Groups["value"]
                        .Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                )
                {
                    // Skip anything that is not a plain class token (JS template fragments).
                    if (!Regex.IsMatch(name, @"^[A-Za-z][\w-]*$"))
                    {
                        continue;
                    }

                    if (!styled.Contains(name) && !scriptApplied.Contains(name))
                    {
                        unstyled.Add($"{Path.GetRelativePath(RepositoryRoot, file)} → .{name}");
                    }
                }
            }
        }

        Assert.True(
            unstyled.Count == 0,
            $"Markup uses classes with no rule in styles.css:{Environment.NewLine}"
                + string.Join(Environment.NewLine, unstyled)
        );
    }
}
