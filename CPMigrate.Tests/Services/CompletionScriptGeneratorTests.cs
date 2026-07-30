using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Completion scripts are generated from the option metadata rather than hand-written, because a
/// hand-written script is wrong the moment an option is added and nobody notices — and a stale
/// completion list is worse than none, since it actively suggests flags that no longer exist. These
/// tests pin the properties that make the generated scripts usable, and that they stay in step with
/// the real option list.
/// </summary>
public class CompletionScriptGeneratorTests
{
    [Theory]
    [InlineData(CompletionShell.Bash)]
    [InlineData(CompletionShell.Zsh)]
    [InlineData(CompletionShell.Fish)]
    [InlineData(CompletionShell.PowerShell)]
    public void Generate_EveryShell_OffersEveryOption(CompletionShell shell)
    {
        // The whole point: the script and the parser cannot disagree, because both read the same
        // [Option] attributes.
        var script = CompletionScriptGenerator.Generate(shell);

        foreach (var option in CompletionScriptGenerator.DescribeOptions())
        {
            var bareName = option.LongName.TrimStart('-');
            script
                .Should()
                .Contain(
                    bareName,
                    $"{option.LongName} exists on Options, so the {shell} script must offer it"
                );
        }
    }

    [Theory]
    [InlineData(CompletionShell.Bash)]
    [InlineData(CompletionShell.Zsh)]
    [InlineData(CompletionShell.Fish)]
    [InlineData(CompletionShell.PowerShell)]
    public void Generate_IsDeterministic(CompletionShell shell)
    {
        // A user commits the generated script; regenerating an unchanged one must produce no diff.
        CompletionScriptGenerator
            .Generate(shell)
            .Should()
            .Be(CompletionScriptGenerator.Generate(shell));
    }

    [Theory]
    [InlineData(CompletionShell.Bash)]
    [InlineData(CompletionShell.Zsh)]
    [InlineData(CompletionShell.Fish)]
    [InlineData(CompletionShell.PowerShell)]
    public void Generate_ExplainsHowToInstallItself(CompletionShell shell)
    {
        // A script with no installation instructions gets piped somewhere useless and abandoned.
        CompletionScriptGenerator.Generate(shell).Should().Contain("cpmigrate --completions");
    }

    [Fact]
    public void Generate_Bash_RegistersAgainstTheCommand()
    {
        var script = CompletionScriptGenerator.Generate(CompletionShell.Bash);

        script.Should().Contain("complete -F _cpmigrate_completions cpmigrate");
        script.Should().Contain("COMPREPLY");
    }

    [Fact]
    public void Generate_Zsh_UsesCompdefAndShowsHelpText()
    {
        var script = CompletionScriptGenerator.Generate(CompletionShell.Zsh);

        script.Should().StartWith("#compdef cpmigrate");
        script.Should().Contain("_arguments");
        // Showing the help text inline is most of what a zsh completion is for.
        script.Should().Contain("[Path to a .sln/.slnx file");
    }

    [Fact]
    public void Generate_Fish_DeclaresShortAndLongForms()
    {
        var script = CompletionScriptGenerator.Generate(CompletionShell.Fish);

        script.Should().Contain("complete -c cpmigrate -s s -l solution");
        script.Should().Contain("-d '");
    }

    [Fact]
    public void Generate_PowerShell_RegistersANativeCompleter()
    {
        var script = CompletionScriptGenerator.Generate(CompletionShell.PowerShell);

        script.Should().Contain("Register-ArgumentCompleter -Native -CommandName cpmigrate");
        script.Should().Contain("CompletionResult");
    }

    [Fact]
    public void Generate_PowerShell_CompletesOptionValuesRatherThanMoreFlags()
    {
        // Once the user has chosen --output, offering the flag list again is the least useful thing
        // the completer could say.
        var script = CompletionScriptGenerator.Generate(CompletionShell.PowerShell);

        script.Should().Contain("$optionValues");
        script.Should().Contain("'--output' = @('Terminal', 'Json', 'Sarif', 'Markdown')");
        script.Should().Contain("ParameterValue");
        script.Should().Contain("$pathOptions");
        script.Should().Contain("ProviderItem", "path options should offer files");
        script.Should().Contain("$commandAst.CommandElements", "the preceding token decides");
    }

    [Theory]
    [InlineData(CompletionShell.Bash)]
    [InlineData(CompletionShell.Zsh)]
    [InlineData(CompletionShell.Fish)]
    [InlineData(CompletionShell.PowerShell)]
    public void Generate_EnumOptions_OfferTheirValues(CompletionShell shell)
    {
        // Otherwise the user has to remember that it is "Sarif" and not "SARIF".
        var script = CompletionScriptGenerator.Generate(shell);

        script.Should().Contain("Terminal");
        script.Should().Contain("Sarif");
        script.Should().Contain("Markdown");
    }

    [Fact]
    public void DescribeOptions_MarksSwitchesAsTakingNoValue()
    {
        // Getting this wrong is what makes a completion feel broken: the shell either swallows the
        // next word or refuses to offer one.
        var options = CompletionScriptGenerator.DescribeOptions();

        options.Single(o => o.LongName == "--analyze").TakesValue.Should().BeFalse();
        options.Single(o => o.LongName == "--solution").TakesValue.Should().BeTrue();
        options.Single(o => o.LongName == "--retention").TakesValue.Should().BeTrue();
    }

    [Fact]
    public void DescribeOptions_ExposesEnumCandidates()
    {
        var output = CompletionScriptGenerator
            .DescribeOptions()
            .Single(o => o.LongName == "--output");

        output.EnumValues.Should().Contain(new[] { "Terminal", "Json", "Sarif", "Markdown" });
    }

    [Fact]
    public void DescribeOptions_KeepsShortFormsWhereTheyExist()
    {
        var options = CompletionScriptGenerator.DescribeOptions();

        options.Single(o => o.LongName == "--solution").ShortName.Should().Be("-s");
        options.Single(o => o.LongName == "--transitive").ShortName.Should().BeNull();
    }

    [Fact]
    public void Generate_Bash_CompletesFilenamesForPathOptions()
    {
        // A path option that offers flag names instead of files is actively unhelpful.
        var script = CompletionScriptGenerator.Generate(CompletionShell.Bash);

        script.Should().Contain("compgen -f");
        script.Should().Contain("--solution");
    }

    [Fact]
    public void Generate_Bash_DoesNotWordSplitCandidates()
    {
        // COMPREPLY=($(compgen …)) word-splits, so "with space.sln" arrives as two useless
        // candidates. mapfile reads a line at a time.
        var script = CompletionScriptGenerator.Generate(CompletionShell.Bash);

        script.Should().Contain("mapfile -t COMPREPLY < <(compgen");
        script.Should().NotContain("COMPREPLY=($(compgen", "this form word-splits");
    }

    [Fact]
    public void Generate_PowerShell_OffersShortFormsToo()
    {
        // `cpmigrate -<Tab>` should suggest -s, -p, -a as the other shells do.
        var script = CompletionScriptGenerator.Generate(CompletionShell.PowerShell);

        script.Should().Contain("Name = '-s'");
        script.Should().Contain("Name = '-a'");
    }

    [Fact]
    public void Generate_PowerShell_PathCompletionKeepsTheTypedPrefix()
    {
        // Returning only the leaf name replaces "src/Ap" with "App.csproj" and silently produces the
        // wrong path.
        var script = CompletionScriptGenerator.Generate(CompletionShell.PowerShell);

        script.Should().Contain("$prefix");
        script.Should().Contain("Split-Path -Parent $wordToComplete");
        script.Should().Contain("$text = \"$prefix$($_.Name)\"");
    }

    [Fact]
    public void Generate_Zsh_EscapesCharactersThatWouldBreakTheSpec()
    {
        // Help text contains brackets and colons, both of which are structural in a zsh spec.
        var script = CompletionScriptGenerator.Generate(CompletionShell.Zsh);

        script.Should().NotContain("[Output format: Terminal", "an unescaped colon ends the spec");
    }

    [Theory]
    [InlineData(CompletionShell.Bash, "bash")]
    [InlineData(CompletionShell.Zsh, "zsh")]
    public void Generate_ProducesSyntacticallyValidShellScript(
        CompletionShell shell,
        string interpreter
    )
    {
        // Structural assertions cannot catch an unbalanced quote or a missing `esac`. Asking the shell
        // itself can — but only where there is a real shell to ask. On a Windows runner `bash` resolves
        // to the WSL launcher stub, which exits non-zero for any input, so its verdict has to be
        // earned: a shell that cannot parse `echo ok` is not one whose opinion means anything.
        if (!CanParse(interpreter, "echo ok\n"))
        {
            return;
        }

        var script = CompletionScriptGenerator.Generate(shell);

        CanParse(interpreter, script)
            .Should()
            .BeTrue($"{interpreter} must accept the generated script");
    }

    [Fact]
    public void Generate_SyntaxCheckActuallyRejectsBrokenInput()
    {
        // Guards the guard: a check that passes everything would silently stop protecting anything.
        if (!CanParse("bash", "echo ok\n"))
        {
            return;
        }

        CanParse("bash", "if [ x ; then\n").Should().BeFalse();
    }

    /// <summary>
    /// Runs <c>&lt;interpreter&gt; -n</c> over a script fed through stdin, returning whether it parsed.
    /// stdin rather than a temp file because Git Bash on Windows cannot open a Windows path.
    /// </summary>
    private static bool CanParse(string interpreter, string script)
    {
        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(interpreter, ["-n"])
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                }
            );
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        if (process is null)
        {
            return false;
        }

        process.StandardInput.Write(script);
        process.StandardInput.Close();
        process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(milliseconds: 30_000);

        return process.ExitCode == 0;
    }

    [Fact]
    public void Generate_UnknownShell_Throws()
    {
        var generate = () => CompletionScriptGenerator.Generate((CompletionShell)99);

        generate.Should().Throw<ArgumentOutOfRangeException>();
    }
}
