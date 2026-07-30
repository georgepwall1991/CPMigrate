using System.Reflection;
using System.Text;
using CommandLine;

namespace CPMigrate.Services;

/// <summary>
/// Shells CPMigrate can generate a completion script for.
/// </summary>
public enum CompletionShell
{
    /// <summary>Bash, sourced from <c>~/.bashrc</c> or a completions directory.</summary>
    Bash,

    /// <summary>Zsh, using its own <c>compdef</c> mechanism.</summary>
    Zsh,

    /// <summary>Fish, whose completions are declared one <c>complete</c> call at a time.</summary>
    Fish,

    /// <summary>PowerShell, via <c>Register-ArgumentCompleter</c>.</summary>
    PowerShell,
}

/// <summary>
/// Generates shell completion scripts from the option metadata on <see cref="Options"/>.
///
/// Generated rather than hand-written, because a hand-written script is wrong the moment an option
/// is added and nobody notices — a stale completion list is worse than none, since it actively
/// suggests flags that no longer exist. Reflecting over the same <c>[Option]</c> attributes that
/// drive parsing means the two cannot disagree.
/// </summary>
public static class CompletionScriptGenerator
{
    /// <summary>The command name completions are registered against.</summary>
    private const string CommandName = "cpmigrate";

    /// <summary>
    /// Generates a completion script.
    /// </summary>
    /// <param name="shell">The shell to target.</param>
    /// <returns>A script to source or install.</returns>
    public static string Generate(CompletionShell shell)
    {
        var options = DescribeOptions();

        return shell switch
        {
            CompletionShell.Bash => GenerateBash(options),
            CompletionShell.Zsh => GenerateZsh(options),
            CompletionShell.Fish => GenerateFish(options),
            CompletionShell.PowerShell => GeneratePowerShell(options),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unsupported shell."),
        };
    }

    /// <summary>
    /// Reads every option off <see cref="Options"/>, in a stable order so regenerating an unchanged
    /// script produces no diff.
    /// </summary>
    internal static IReadOnlyList<CompletionOption> DescribeOptions()
    {
        return typeof(Options)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property =>
                (Property: property, Attribute: property.GetCustomAttribute<OptionAttribute>())
            )
            .Where(entry =>
                entry.Attribute is not null && !string.IsNullOrEmpty(entry.Attribute.LongName)
            )
            .Select(entry => new CompletionOption(
                $"--{entry.Attribute!.LongName}",
                string.IsNullOrEmpty(entry.Attribute.ShortName)
                    ? null
                    : $"-{entry.Attribute.ShortName}",
                entry.Attribute.HelpText ?? string.Empty,
                TakesValue(entry.Property),
                EnumValues(entry.Property)
            ))
            .OrderBy(option => option.LongName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A boolean switch takes no value; everything else does. Getting this wrong is what makes a
    /// completion feel broken — the shell either swallows the next word or refuses to offer one.
    /// </summary>
    private static bool TakesValue(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return type != typeof(bool);
    }

    /// <summary>
    /// Candidate values for an enum-valued option, so the shell can complete <c>--output Sa…</c>
    /// rather than leaving the user to remember the spelling.
    /// </summary>
    private static IReadOnlyList<string> EnumValues(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return type.IsEnum ? Enum.GetNames(type) : Array.Empty<string>();
    }

    private static string GenerateBash(IReadOnlyList<CompletionOption> options)
    {
        var script = new StringBuilder();
        var flags = string.Join(' ', options.SelectMany(o => o.AllNames()));

        script.AppendLine(
            "# CPMigrate bash completion. Source it, or drop it in a completions directory:"
        );
        script.AppendLine(
            $"#   cpmigrate --completions bash > /usr/local/etc/bash_completion.d/{CommandName}"
        );
        script.AppendLine();
        script.AppendLine($"_{CommandName}_completions() {{");
        script.AppendLine("  local current previous");
        script.AppendLine("  current=\"${COMP_WORDS[COMP_CWORD]}\"");
        script.AppendLine("  previous=\"${COMP_WORDS[COMP_CWORD-1]}\"");
        script.AppendLine();
        script.AppendLine("  case \"$previous\" in");

        foreach (var option in options.Where(o => o.EnumValues.Count > 0))
        {
            script.AppendLine($"    {string.Join('|', option.AllNames())})");
            script.AppendLine(
                $"      mapfile -t COMPREPLY < <(compgen -W \"{string.Join(' ', option.EnumValues)}\" -- \"$current\")"
            );
            script.AppendLine("      return 0;;");
        }

        // A path-valued option should complete filenames, not flags.
        var pathOptions = options.Where(IsPathLike).SelectMany(o => o.AllNames()).ToList();
        if (pathOptions.Count > 0)
        {
            script.AppendLine($"    {string.Join('|', pathOptions)})");
            // mapfile, not COMPREPLY=($(…)): the command substitution word-splits, turning
            // "with space.sln" into two useless candidates.
            script.AppendLine("      mapfile -t COMPREPLY < <(compgen -f -- \"$current\")");
            script.AppendLine("      return 0;;");
        }

        script.AppendLine("  esac");
        script.AppendLine();
        script.AppendLine($"  mapfile -t COMPREPLY < <(compgen -W \"{flags}\" -- \"$current\")");
        script.AppendLine("}");
        script.AppendLine();
        script.AppendLine($"complete -F _{CommandName}_completions {CommandName}");

        return script.ToString();
    }

    private static string GenerateZsh(IReadOnlyList<CompletionOption> options)
    {
        var script = new StringBuilder();

        script.AppendLine("#compdef cpmigrate");
        script.AppendLine("# CPMigrate zsh completion. Install into a directory on $fpath:");
        script.AppendLine($"#   cpmigrate --completions zsh > \"${{fpath[1]}}/_{CommandName}\"");
        script.AppendLine();
        script.AppendLine($"_{CommandName}() {{");
        script.AppendLine("  _arguments -s \\");

        foreach (var option in options)
        {
            // Zsh shows the help text inline while completing, which is most of the value here.
            var description = EscapeZsh(option.HelpText);
            var valueSpec = DescribeZshValue(option);

            foreach (var name in option.AllNames())
            {
                script.AppendLine($"    '{name}[{description}]{valueSpec}' \\");
            }
        }

        script.AppendLine("    && return 0");
        script.AppendLine("}");
        script.AppendLine();
        script.AppendLine($"_{CommandName} \"$@\"");

        return script.ToString();
    }

    /// <summary>
    /// The zsh value specification for an option: a fixed set for an enum, filename completion for a
    /// path, a bare value otherwise, and nothing at all for a switch.
    /// </summary>
    private static string DescribeZshValue(CompletionOption option)
    {
        if (option.EnumValues.Count > 0)
        {
            return $":value:({string.Join(' ', option.EnumValues)})";
        }

        if (!option.TakesValue)
        {
            return string.Empty;
        }

        return IsPathLike(option) ? ":path:_files" : ":value:";
    }

    private static string GenerateFish(IReadOnlyList<CompletionOption> options)
    {
        var script = new StringBuilder();

        script.AppendLine("# CPMigrate fish completion. Install it with:");
        script.AppendLine(
            $"#   cpmigrate --completions fish > ~/.config/fish/completions/{CommandName}.fish"
        );
        script.AppendLine();

        foreach (var option in options)
        {
            var parts = new List<string> { $"complete -c {CommandName}" };

            if (option.ShortName is not null)
            {
                parts.Add($"-s {option.ShortName.TrimStart('-')}");
            }

            parts.Add($"-l {option.LongName.TrimStart('-')}");
            parts.Add($"-d '{EscapeFish(option.HelpText)}'");

            if (option.EnumValues.Count > 0)
            {
                parts.Add($"-x -a '{string.Join(' ', option.EnumValues)}'");
            }
            else if (option.TakesValue)
            {
                // -r requires an argument; -F lets fish offer filenames for it.
                parts.Add(IsPathLike(option) ? "-r -F" : "-r");
            }

            script.AppendLine(string.Join(' ', parts));
        }

        return script.ToString();
    }

    private static string GeneratePowerShell(IReadOnlyList<CompletionOption> options)
    {
        var script = new StringBuilder();

        script.AppendLine("# CPMigrate PowerShell completion. Add it to your profile:");
        script.AppendLine("#   cpmigrate --completions powershell >> $PROFILE");
        script.AppendLine();
        script.AppendLine(
            $"Register-ArgumentCompleter -Native -CommandName {CommandName} -ScriptBlock {{"
        );
        script.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        script.AppendLine();
        script.AppendLine("    $options = @(");

        foreach (var option in options)
        {
            // Short forms included, so `cpmigrate -<Tab>` suggests -s, -p, -a as the other shells do.
            foreach (var name in option.AllNames())
            {
                script.AppendLine(
                    $"        @{{ Name = '{name}'; Tooltip = '{EscapePowerShell(option.HelpText)}' }}"
                );
            }
        }

        script.AppendLine("    )");
        script.AppendLine();

        // Values a preceding option expects. Without this the completer offers the flag list again,
        // which is the least useful thing it could say once the user has already chosen an option.
        script.AppendLine("    $optionValues = @{");
        foreach (var option in options.Where(o => o.EnumValues.Count > 0))
        {
            var values = string.Join(", ", option.EnumValues.Select(v => $"'{v}'"));
            script.AppendLine($"        '{option.LongName}' = @({values})");

            if (option.ShortName is not null)
            {
                script.AppendLine($"        '{option.ShortName}' = @({values})");
            }
        }
        script.AppendLine("    }");
        script.AppendLine();

        var pathOptions = options.Where(IsPathLike).SelectMany(o => o.AllNames());
        script.AppendLine(
            $"    $pathOptions = @({string.Join(", ", pathOptions.Select(name => $"'{name}'"))})"
        );
        script.AppendLine();
        script.AppendLine("    # The token before the cursor decides whether a value or a flag is wanted.");
        script.AppendLine("    $tokens = $commandAst.CommandElements | ForEach-Object { $_.ToString() }");
        script.AppendLine("    $previous = if ($wordToComplete) {");
        script.AppendLine("        if ($tokens.Count -ge 2) { $tokens[$tokens.Count - 2] } else { $null }");
        script.AppendLine("    } else {");
        script.AppendLine("        $tokens[$tokens.Count - 1]");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    if ($previous -and $optionValues.ContainsKey($previous)) {");
        script.AppendLine("        return $optionValues[$previous] |");
        script.AppendLine("            Where-Object { $_ -like \"$wordToComplete*\" } |");
        script.AppendLine("            ForEach-Object {");
        script.AppendLine(
            "                [System.Management.Automation.CompletionResult]::new("
                + "$_, $_, 'ParameterValue', $_)"
        );
        script.AppendLine("            }");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    if ($previous -and $pathOptions -contains $previous) {");
        // The completion text has to carry the prefix already typed. Returning only the leaf name
        // replaces "src/Ap" with "App.csproj" and silently produces the wrong path.
        script.AppendLine("        $prefix = if ($wordToComplete) {");
        script.AppendLine("            $parent = Split-Path -Parent $wordToComplete");
        script.AppendLine(
            "            if ($parent) { \"$parent$([System.IO.Path]::DirectorySeparatorChar)\" } else { '' }"
        );
        script.AppendLine("        } else { '' }");
        script.AppendLine();
        script.AppendLine(
            "        return Get-ChildItem -Path \"$wordToComplete*\" -ErrorAction SilentlyContinue |"
        );
        script.AppendLine("            ForEach-Object {");
        script.AppendLine("                $text = \"$prefix$($_.Name)\"");
        script.AppendLine("                if ($text -match '\\s') { $text = \"'$text'\" }");
        script.AppendLine(
            "                [System.Management.Automation.CompletionResult]::new("
                + "$text, $_.Name, 'ProviderItem', $_.FullName)"
        );
        script.AppendLine("            }");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    $options |");
        script.AppendLine("        Where-Object { $_.Name -like \"$wordToComplete*\" } |");
        script.AppendLine("        ForEach-Object {");
        script.AppendLine(
            "            [System.Management.Automation.CompletionResult]::new("
                + "$_.Name, $_.Name, 'ParameterName', $_.Tooltip)"
        );
        script.AppendLine("        }");
        script.AppendLine("}");

        return script.ToString();
    }

    /// <summary>
    /// Whether an option names a file or directory, so the shell offers paths instead of nothing.
    /// Inferred from the option name because that is where the intent actually lives — the property
    /// type is just <c>string</c> either way.
    /// </summary>
    private static bool IsPathLike(CompletionOption option)
    {
        return option.LongName.Contains("dir", StringComparison.OrdinalIgnoreCase)
            || option.LongName.Contains("file", StringComparison.OrdinalIgnoreCase)
            || option.LongName is "--solution" or "--project" or "--baseline";
    }

    private static string EscapeZsh(string text)
    {
        return text.Replace("'", "'\\''", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);
    }

    private static string EscapeFish(string text)
    {
        return text.Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string EscapePowerShell(string text)
    {
        return text.Replace("'", "''", StringComparison.Ordinal);
    }
}

/// <summary>
/// One completable option, reduced to what a shell script needs.
/// </summary>
/// <param name="LongName">The long form, including its leading dashes.</param>
/// <param name="ShortName">The short form, or null when there is none.</param>
/// <param name="HelpText">Description shown while completing.</param>
/// <param name="TakesValue">Whether the option expects a following value.</param>
/// <param name="EnumValues">Candidate values, for an enum-valued option.</param>
internal record CompletionOption(
    string LongName,
    string? ShortName,
    string HelpText,
    bool TakesValue,
    IReadOnlyList<string> EnumValues
)
{
    /// <summary>Long form first, then the short form when one exists.</summary>
    public IEnumerable<string> AllNames()
    {
        yield return LongName;

        if (ShortName is not null)
        {
            yield return ShortName;
        }
    }
}
