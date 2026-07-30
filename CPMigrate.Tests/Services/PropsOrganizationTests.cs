using System.Globalization;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Directory.Packages.props is a committed file that several people and a CI job all regenerate. That
/// makes its byte-for-byte shape part of the contract, not a cosmetic detail: an entry that lands in a
/// different place on a different machine, or a run that appends instead of inserting, shows up as a
/// diff nobody wrote and a merge conflict nobody caused.
///
/// These tests pin the two properties that matter — the same input produces the same file anywhere, and
/// a file that was ordered stays ordered — plus the thing a generator has no right to destroy: the
/// comments a team wrote to explain why a version is pinned.
/// </summary>
public class PropsOrganizationTests : IDisposable
{
    private readonly string _testDirectory;

    public PropsOrganizationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateProps_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- determinism

    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    [InlineData("tr-TR")]
    [InlineData("cs-CZ")]
    public void Generate_OrdersIdentically_WhateverTheMachinesCulture(string culture)
    {
        // `OrderBy(x => x.Key)` uses a culture-sensitive comparer. Measured against the package IDs
        // below, ICU's collation and case-insensitive ordinal happen to agree for every locale checked
        // here except tr-TR, where the dotted/dotless I reorders — so this is a latent hazard rather
        // than an ordering anyone has been bitten by, and it is pinned as such. What makes it worth
        // removing anyway is what it depends on: the ambient culture, the ICU version on the box, and
        // whether the host was built with invariant globalization (which silently falls back to
        // ordinal). None of those belong in the byte order of a committed file.
        var packages = Packages(
            ("Microsoft.Extensions.Logging", "10.0.2"),
            ("Microsoft.Extensions.Logging.Abstractions", "10.0.2"),
            ("Microsoft.ExtensionsLogging.Shim", "1.0.0"),
            ("Microsoft-Extensions-Legacy", "1.0.0"),
            ("microsoft.extensions.hosting", "10.0.2"),
            ("System.Text.Json", "10.0.0"),
            ("SystemTextJson.Compat", "1.0.0")
        );

        var content = InCulture(culture, () => new PropsGenerator().Generate(packages));

        content.Should().Be(InCulture("en-US", () => new PropsGenerator().Generate(packages)));
    }

    [Fact]
    public void Generate_OrdersByOrdinal_SoDotsAndHyphensSortWhereTheyActuallyAre()
    {
        // Pins the actual order, so it is a decision rather than whatever the comparer happens to do:
        // '-' (0x2D) before '.' (0x2E) before letters, which is where those characters really are and
        // what `sort` and `git diff` will agree with.
        var content = new PropsGenerator().Generate(
            Packages(
                ("Serilog.Sinks.File", "7.0.0"),
                ("Serilog", "4.3.0"),
                ("Serilog.Extensions.Logging", "10.0.0"),
                ("SerilogTimings", "3.1.0"),
                ("Serilog-Legacy", "1.0.0")
            )
        );

        PackageIdsInOrder(content)
            .Should()
            .Equal(
                "Serilog",
                "Serilog-Legacy",
                "Serilog.Extensions.Logging",
                "Serilog.Sinks.File",
                "SerilogTimings"
            );
    }

    [Fact]
    public void Generate_IsCaseInsensitiveOnTies_ButStableAndTotal()
    {
        // Package IDs are case-insensitive to NuGet, so ordering purely by ordinal would put every
        // lower-cased ID in a block after the upper-cased ones — "xunit" landing after "Serilog" reads
        // as unsorted to anyone scanning the file. Compare case-insensitively, then break exact ties
        // ordinally so the result is still a total order and cannot depend on input enumeration order.
        var content = new PropsGenerator().Generate(
            Packages(("xunit", "2.9.0"), ("Serilog", "4.3.0"), ("Moq", "4.20.0"))
        );

        PackageIdsInOrder(content).Should().Equal("Moq", "Serilog", "xunit");
    }

    // ---------------------------------------------------------------- merge keeps order

    [Fact]
    public void MergeExisting_InsertsNewPackagesInOrder_RatherThanAppendingThem()
    {
        // The failure this prevents: migrate once, get a sorted file; add a package and migrate again,
        // and the new entry lands at the bottom. Repeat a few times and the file is sorted at the top
        // and chronological at the bottom, so every subsequent addition produces a diff in a place that
        // has nothing to do with the package being added.
        var propsPath = WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <PackageVersion Include="Delta" Version="1.0.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, added, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"), ("Echo", "2.0.0"))
        );

        added.Should().Be(2);
        PackageIdsInOrder(content).Should().Equal("Alpha", "Bravo", "Delta", "Echo", "Zulu");
    }

    [Fact]
    public void MergeExisting_LeavesAnUnsortedFileAlone_ApartFromWhereItPutsTheNewEntry()
    {
        // Reordering someone's file because it did not match our preference would produce a diff far
        // larger than the change they asked for, and would move comments away from what they document.
        // A file that was not sorted gets the new entry appended, and nothing else moves.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Zulu" Version="1.0.0" />
                <PackageVersion Include="Alpha" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Mike", "2.0.0"))
        );

        PackageIdsInOrder(content).Should().Equal("Zulu", "Alpha", "Mike");
    }

    // ---------------------------------------------------------------- comments survive

    [Fact]
    public void MergeExisting_KeepsCommentsWithTheEntryTheyDocument()
    {
        // A comment above a pin is the only record of why the pin exists. Inserting an entry between a
        // comment and its item silently reattaches the explanation to the wrong package — which is worse
        // than losing it, because the file still reads as if it were correct.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <!-- Pinned: 2.x drops netstandard2.0, see #412 -->
                <PackageVersion Include="Charlie" Version="1.9.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        content.Should().Contain("Pinned: 2.x drops netstandard2.0, see #412");

        // Bravo's sorted position is between Alpha and Charlie, which is precisely where the comment
        // lives — and MSBuild cannot address a position before a comment, because it cannot see one. So
        // Bravo goes one slot later. The comment staying with Charlie is the property worth keeping;
        // strict ordering is the one worth giving up, since a misattributed "why" reads as correct.
        var lines = SignificantLines(content);
        var comment = lines.FindIndex(l => l.Contains("Pinned: 2.x"));
        comment.Should().BeGreaterThan(-1);
        lines[comment - 1].Should().Contain("Alpha");
        lines[comment + 1].Should().Contain("Charlie");
        PackageIdsInOrder(content).Should().Equal("Alpha", "Charlie", "Bravo", "Zulu");
    }

    [Fact]
    public void MergeExisting_StillInsertsInOrder_WhenTheSortedPositionHasNoCommentInIt()
    {
        // The concession above applies only where a comment is actually in the way. An entry landing
        // anywhere else still goes exactly where it sorts.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <!-- Pinned: 2.x drops netstandard2.0, see #412 -->
                <PackageVersion Include="Charlie" Version="1.9.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Mike", "2.0.0"))
        );

        PackageIdsInOrder(content).Should().Equal("Alpha", "Charlie", "Mike", "Zulu");
        SignificantLines(content)[
            SignificantLines(content).FindIndex(l => l.Contains("Pinned: 2.x")) + 1
        ]
            .Should()
            .Contain("Charlie");
    }

    [Fact]
    public void MergeExisting_KeepsSortingAfterAPositionWasForcedByAComment()
    {
        // Honouring a comment costs one entry its exact position. If that counted as "this file is not
        // sorted", the next merge would read the position it had itself just forced as evidence, give up,
        // and append everything from then on — so a single comment would permanently degrade the file.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <!-- Pinned: 14.x needs net10, see #412 -->
                <PackageVersion Include="Charlie" Version="1.9.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var generator = new PropsGenerator();
        var (first, _, _, _) = generator.MergeExisting(propsPath, Packages(("Bravo", "2.0.0")));
        File.WriteAllText(propsPath, first);
        // Alpha, Charlie, Bravo, Zulu — Bravo was pushed past Charlie's comment.

        var (second, added, _, _) = generator.MergeExisting(
            propsPath,
            Packages(("Delta", "3.0.0"))
        );

        added.Should().Be(1);
        PackageIdsInOrder(second).Should().Equal("Alpha", "Charlie", "Bravo", "Delta", "Zulu");
    }

    [Fact]
    public void MergeExisting_KeepsSortingWhenAPinWasPushedPastSeveralCommentsAtOnce()
    {
        // Cross-review caught this: a pin whose sorted position sits before *two or more* consecutive
        // commented entries is moved past all of them, so undoing only the last step left the sequence
        // out of order and the next merge read this code's own output as hand-arranged — appending
        // everything from then on.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <!-- why Charlie -->
                <PackageVersion Include="Charlie" Version="1.0.0" />
                <!-- why Delta -->
                <PackageVersion Include="Delta" Version="1.0.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var generator = new PropsGenerator();
        var (first, _, _, _) = generator.MergeExisting(propsPath, Packages(("Bravo", "2.0.0")));
        PackageIdsInOrder(first).Should().Equal("Alpha", "Charlie", "Delta", "Bravo", "Zulu");
        File.WriteAllText(propsPath, first);

        // Both comments still describe their own pins, and the file is still treated as ordered.
        var lines = SignificantLines(first);
        lines[lines.FindIndex(l => l.Contains("why Charlie")) + 1].Should().Contain("Charlie");
        lines[lines.FindIndex(l => l.Contains("why Delta")) + 1].Should().Contain("Delta");

        var (second, added, _, _) = generator.MergeExisting(propsPath, Packages(("Echo", "3.0.0")));

        added.Should().Be(1);
        PackageIdsInOrder(second)
            .Should()
            .Equal("Alpha", "Charlie", "Delta", "Bravo", "Echo", "Zulu");
    }

    [Fact]
    public void MergeExisting_DoesNotMistakeADocumentingCommentForAHeader_WhenAnotherItemOpensTheGroup()
    {
        // Cross-review caught this: the header exemption keyed off "first PackageVersion" rather than
        // "first child of the group". A group can open with something else — a GlobalPackageReference,
        // here — and the comment below it documents the pin it sits above, so treating the first pin as
        // the header case handed that explanation to the new entry.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <GlobalPackageReference Include="Nerdbank.GitVersioning" Version="3.6.0" />
                <!-- Pinned: why Charlie is held back -->
                <PackageVersion Include="Charlie" Version="1.9.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        var lines = SignificantLines(content);
        lines[lines.FindIndex(l => l.Contains("why Charlie is held back")) + 1]
            .Should()
            .Contain(
                "Charlie",
                "the comment documents Charlie, not whatever gets inserted above it"
            );
        PackageIdsInOrder(content).Should().Equal("Charlie", "Bravo", "Zulu");
    }

    [Fact]
    public void MergeExisting_StillTreatsAHandArrangedFileAsUnordered_WhenAnInversionIsUnrelatedToAComment()
    {
        // Cross-review caught this: exempting *any* inversion that follows a commented entry hid
        // inversions a comment had nothing to do with, so a hand-arranged file was read as sorted and its
        // arrangement was overridden. The exemption undoes only the specific displacement this class
        // creates — a pin pushed past one commented entry — and the result must then be ordered outright.
        // Here Mike > Bravo cannot be that: undoing it gives Alpha, Bravo, Mike, Charlie, which is still
        // out of order at Mike > Charlie.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <!-- some reason -->
                <PackageVersion Include="Mike" Version="1.0.0" />
                <PackageVersion Include="Bravo" Version="1.0.0" />
                <PackageVersion Include="Charlie" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Delta", "2.0.0"))
        );

        PackageIdsInOrder(content).Should().Equal("Alpha", "Mike", "Bravo", "Charlie", "Delta");
    }

    [Fact]
    public void MergeExisting_TreatsAnInversionBeforeACommentAsUnordered()
    {
        // The commented entry is the *second* of the inverted pair, so no displacement of this class
        // could have produced it. Nothing is exempted.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Zulu" Version="1.0.0" />
                <!-- some reason -->
                <PackageVersion Include="Alpha" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        PackageIdsInOrder(content).Should().Equal("Zulu", "Alpha", "Bravo");
    }

    [Fact]
    public void MergeExisting_KeepsSortingAcrossSeveralCommentForcedPositions()
    {
        // More than one comment must not accumulate into "this file is unsorted".
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <!-- why Charlie -->
                <PackageVersion Include="Charlie" Version="1.0.0" />
                <PackageVersion Include="Bravo" Version="1.0.0" />
                <!-- why Golf -->
                <PackageVersion Include="Golf" Version="1.0.0" />
                <PackageVersion Include="Echo" Version="1.0.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, added, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Hotel", "2.0.0"))
        );

        added.Should().Be(1);
        PackageIdsInOrder(content)
            .Should()
            .Equal("Alpha", "Charlie", "Bravo", "Golf", "Echo", "Hotel", "Zulu");
    }

    [Fact]
    public void MergeExisting_TreatsALeadingCommentAsAGroupHeader_AndKeepsItAtTheTop()
    {
        // A comment that is the group's first child reads as a header for the group, not for whatever
        // entry happens to be listed first. There is no way to tell the two apart, and holding the first
        // slot open is the reading that leaves an ordered file ordered.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <!-- Versions are managed centrally. Edit here, not in project files. -->
                <PackageVersion Include="Bravo" Version="1.0.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Alpha", "2.0.0"))
        );

        PackageIdsInOrder(content).Should().Equal("Alpha", "Bravo", "Zulu");
        var lines = SignificantLines(content);
        lines[lines.FindIndex(l => l.Contains("managed centrally")) + 1]
            .Should()
            .Contain("Alpha", "the header stays above the list rather than below its first entry");
    }

    [Fact]
    public void MergeExisting_WritesNewPinsInTheSameStyleAsEveryOtherEntry()
    {
        // AddMetadata defaults to element form, so a merge produced
        // <PackageVersion Include="Bravo"><Version>2.0.0</Version></PackageVersion> alongside entries
        // using Version="…" — one file, two styles for the same thing, and a three-line diff for a
        // one-line addition.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        content.Should().Contain("""<PackageVersion Include="Bravo" Version="2.0.0" />""");
        content.Should().NotContain("<Version>");
    }

    [Fact]
    public void MergeExisting_MatchesAConsistentlyElementFormFile_RatherThanImposingAttributes()
    {
        // Cross-review caught this: writing new pins as attributes unconditionally recreates the same
        // mixture, only in the other direction. A file written consistently in element form has expressed
        // an opinion, and a merge is not the place to overrule it.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha">
                  <Version>1.0.0</Version>
                </PackageVersion>
                <PackageVersion Include="Zulu">
                  <Version>1.0.0</Version>
                </PackageVersion>
              </ItemGroup>
            </Project>
            """
        );

        var (content, added, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        added.Should().Be(1);
        content.Should().Contain("<Version>2.0.0</Version>");
        content.Should().NotContain("""Include="Bravo" Version=""");
    }

    [Fact]
    public void MergeExisting_UsesAttributeForm_WhenTheFileAlreadyMixesBothStyles()
    {
        // A file with no consistent style has no opinion to honour, so new pins take the form Generate
        // produces and NuGet's own documentation uses.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha">
                  <Version>1.0.0</Version>
                </PackageVersion>
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        content.Should().Contain("""<PackageVersion Include="Bravo" Version="2.0.0" />""");
    }

    [Fact]
    public void MergeExisting_LeavesAnExistingElementFormPinInElementForm()
    {
        // Rewriting an entry someone wrote as a child element into attribute form would be a diff they
        // did not ask for, in a file they may have formatted deliberately. Only new pins pick a style.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha">
                  <Version>1.0.0</Version>
                </PackageVersion>
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, updated, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Alpha", "2.0.0"))
        );

        updated.Should().Be(1);
        content.Should().Contain("<Version>2.0.0</Version>");
    }

    [Fact]
    public void MergeExisting_PreservesEveryOtherKindOfContentInTheFile()
    {
        // MergeExisting rewrites the whole document through MSBuild's object model, so anything the
        // model does not round-trip is silently dropped. A props file routinely carries more than
        // PackageVersion entries.
        var propsPath = WriteProps(
            """
            <Project>
              <!-- Managed by CPMigrate; edit versions here, not in project files. -->
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
              <ItemGroup>
                <GlobalPackageReference Include="Nerdbank.GitVersioning" Version="3.6.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageVersion Include="Legacy" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, hasConditional) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        hasConditional.Should().BeTrue("the conditional group must still be reported");
        content.Should().Contain("Managed by CPMigrate");
        content.Should().Contain("CentralPackageTransitivePinningEnabled");
        content.Should().Contain("GlobalPackageReference");
        content.Should().Contain("Nerdbank.GitVersioning");
        content.Should().Contain("'$(TargetFramework)' == 'net8.0'");
        content.Should().Contain("Legacy");
    }

    [Fact]
    public void MergeExisting_AddsToTheUnconditionalGroup_NotAConditionalOne()
    {
        // Adding an unconditional pin to a framework-conditional group makes it apply to one framework
        // only, which restores fine on the developer's machine and fails on the other target.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageVersion Include="Legacy" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var (content, _, _, _) = new PropsGenerator().MergeExisting(
            propsPath,
            Packages(("Bravo", "2.0.0"))
        );

        var groupStart = content.IndexOf("net8.0", StringComparison.Ordinal);
        var groupEnd = content.IndexOf("</ItemGroup>", groupStart, StringComparison.Ordinal);
        content[groupStart..groupEnd]
            .Should()
            .NotContain("Bravo", "the new pin must not be scoped to a single framework");
    }

    // ---------------------------------------------------------------- idempotence

    [Fact]
    public void MergeExisting_IsIdempotent_SoRunningItTwiceProducesNoSecondDiff()
    {
        // The property a committed generated file needs above all others: running the tool again on its
        // own output changes nothing. Without it, a CI job that regenerates and checks `git diff` fails
        // on a repository nobody touched.
        var propsPath = WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.0.0" />
                <PackageVersion Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var packages = Packages(("Bravo", "2.0.0"), ("Alpha", "1.0.0"));

        var generator = new PropsGenerator();
        var (first, _, _, _) = generator.MergeExisting(propsPath, packages);
        File.WriteAllText(propsPath, first);
        var (second, added, updated, _) = generator.MergeExisting(propsPath, packages);

        added.Should().Be(0);
        updated.Should().Be(0);
        second.Should().Be(first);
    }

    [Fact]
    public void Generate_IsIdempotentAcrossInputOrdering()
    {
        // The input is a dictionary, whose enumeration order is not part of its contract. Two scans that
        // discovered the same packages in a different sequence must still produce the same file.
        var forward = new PropsGenerator().Generate(
            Packages(("Alpha", "1.0.0"), ("Bravo", "2.0.0"), ("Charlie", "3.0.0"))
        );
        var backward = new PropsGenerator().Generate(
            Packages(("Charlie", "3.0.0"), ("Bravo", "2.0.0"), ("Alpha", "1.0.0"))
        );

        backward.Should().Be(forward);
    }

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, HashSet<string>> Packages(
        params (string Name, string Version)[] packages
    )
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version) in packages)
        {
            if (!result.TryGetValue(name, out var versions))
            {
                versions = [];
                result[name] = versions;
            }

            versions.Add(version);
        }

        return result;
    }

    private string WriteProps(string content)
    {
        var path = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(path, content);
        return path;
    }

    private static List<string> PackageIdsInOrder(string content)
    {
        return content
            .Split('\n')
            .Where(line => line.Contains("<PackageVersion", StringComparison.Ordinal))
            .Select(line =>
            {
                var start = line.IndexOf("Include=\"", StringComparison.Ordinal) + 9;
                var end = line.IndexOf('"', start);
                return line[start..end];
            })
            .ToList();
    }

    private static List<string> SignificantLines(string content)
    {
        return content
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static T InCulture<T>(string culture, Func<T> action)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
