using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Moq;
using Spectre.Console.Testing;

namespace CPMigrate.Tests.Services;

/// <summary>
/// The dependency tree is read at a glance, so its shape is the contract: direct before
/// transitive, alphabetical within a group, long transitive lists capped with a remainder,
/// and names that survive characters Spectre would otherwise parse as markup.
/// </summary>
public class DependencyTreeServiceTests
{
    [Fact]
    public async Task RunAsync_EmptyWorkspace_ExitsSuccessWithZeroSummary()
    {
        var console = new Mock<IConsoleService>();
        var service = new DependencyTreeService(console.Object);

        var exit = await service.RunAsync(new ProjectPackageInfo(Array.Empty<PackageReference>()));

        exit.Should().Be(ExitCodes.Success);
        console.Verify(c => c.Banner(It.Is<string>(s => s.Contains("DEPENDENCY TREE"))), Times.Once);
        console.Verify(
            c => c.Dim(It.Is<string>(s => s.Contains("0 project(s), 0 direct, 0 transitive"))),
            Times.Once);
    }

    [Fact]
    public void BuildProjectTree_DirectBeforeTransitiveInNameOrder()
    {
        var tree = DependencyTreeService.BuildProjectTree("App.csproj", new[]
        {
            Ref("Zebra.Direct", "1.0", transitive: false),
            Ref("apple.Transitive", "2.0", transitive: true),
            Ref("Mango.Direct", "3.0", transitive: false),
        });

        var text = Render(tree);
        text.Should().Contain("App.csproj");
        text.Should().Contain("direct (2)");
        text.Should().Contain("transitive (1)");
        text.IndexOf("direct (2)", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("transitive (1)", StringComparison.Ordinal));
        text.IndexOf("Mango.Direct", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("Zebra.Direct", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildProjectTree_EmptyVersion_RendersCentralPlaceholder()
    {
        var tree = DependencyTreeService.BuildProjectTree("App.csproj", new[]
        {
            Ref("Managed.Centrally", string.Empty, transitive: false),
        });

        Render(tree).Should().Contain("(central)");
    }

    [Fact]
    public void BuildProjectTree_ManyTransitive_CapsAtTwentyWithRemainder()
    {
        var refs = Enumerable.Range(1, 23)
            .Select(i => Ref($"T{i:00}.Pkg", "1.0", transitive: true))
            .ToArray();

        var text = Render(DependencyTreeService.BuildProjectTree("App.csproj", refs));

        text.Should().Contain("transitive (23)");
        text.Should().Contain("... and 3 more");
        text.Should().NotContain("T23.Pkg");
    }

    [Fact]
    public void BuildProjectTree_NoPackages_NamesTheEmptyState()
    {
        var text = Render(DependencyTreeService.BuildProjectTree(
            "App.csproj",
            Array.Empty<PackageReference>()));

        text.Should().Contain("no packages");
    }

    [Fact]
    public void BuildProjectTree_MarkupInNames_RendersLiterally()
    {
        var tree = DependencyTreeService.BuildProjectTree("App.csproj", new[]
        {
            Ref("Weird.[bold].Name", "1.0", transitive: false),
        });

        Render(tree).Should().Contain("Weird.[bold].Name");
    }

    private static PackageReference Ref(string name, string version, bool transitive)
    {
        return new PackageReference(name, version, "/repo/App.csproj", "App.csproj", IsTransitive: transitive);
    }

    private static string Render(Spectre.Console.Tree tree)
    {
        var console = new TestConsole().Interactive();
        console.Write(tree);
        return console.Output;
    }
}
