using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Update;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Update;

public class PropsUpdateTransactionTests : IDisposable
{
    private readonly string _directory;
    private readonly string _propsPath;
    private readonly PropsGenerator _propsGenerator = new();

    public PropsUpdateTransactionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"CPMigrateTx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _propsPath = Path.Combine(_directory, "Directory.Packages.props");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ApplyAsync_WritesOnlyTheRequestedSubset()
    {
        WriteProps(("Alpha", "1.0.0"), ("Beta", "2.0.0"));
        var transaction = await BeginAsync(("Alpha", "1.0.0"), ("Beta", "2.0.0"));

        await transaction.ApplyAsync([Update("Alpha", "1.0.0", "1.5.0")]);

        var content = await File.ReadAllTextAsync(_propsPath);
        content.Should().Contain("1.5.0");
        content.Should().Contain("2.0.0", "Beta was not in the subset and must keep its baseline version");
    }

    [Fact]
    public async Task ApplyAsync_IsIndependentOfPreviousApplies()
    {
        // The property bisection depends on: probing {Alpha} after {Alpha,Beta} must not leave Beta bumped.
        WriteProps(("Alpha", "1.0.0"), ("Beta", "2.0.0"));
        var transaction = await BeginAsync(("Alpha", "1.0.0"), ("Beta", "2.0.0"));

        await transaction.ApplyAsync([Update("Alpha", "1.0.0", "1.5.0"), Update("Beta", "2.0.0", "2.5.0")]);
        await transaction.ApplyAsync([Update("Alpha", "1.0.0", "1.5.0")]);

        var content = await File.ReadAllTextAsync(_propsPath);
        content.Should().Contain("1.5.0");
        content.Should().Contain("2.0.0");
        content.Should().NotContain("2.5.0");
    }

    [Fact]
    public async Task RevertAsync_RestoresExactOriginalBytes()
    {
        WriteProps(("Alpha", "1.0.0"));
        var original = await File.ReadAllTextAsync(_propsPath);
        var transaction = await BeginAsync(("Alpha", "1.0.0"));

        await transaction.ApplyAsync([Update("Alpha", "1.0.0", "9.9.9")]);
        await transaction.RevertAsync();

        (await File.ReadAllTextAsync(_propsPath)).Should().Be(original);
    }

    [Fact]
    public async Task ApplyAsync_EmptySubset_RevertsToBaseline()
    {
        WriteProps(("Alpha", "1.0.0"));
        var original = await File.ReadAllTextAsync(_propsPath);
        var transaction = await BeginAsync(("Alpha", "1.0.0"));

        await transaction.ApplyAsync([Update("Alpha", "1.0.0", "9.9.9")]);
        await transaction.ApplyAsync([]);

        (await File.ReadAllTextAsync(_propsPath)).Should().Be(original);
    }

    [Fact]
    public async Task BeginAsync_CopiesBaseline_SoCallerMutationCannotCorruptRevert()
    {
        WriteProps(("Alpha", "1.0.0"));
        var baseline = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alpha"] = ["1.0.0"]
        };
        var transaction = await PropsUpdateTransaction.BeginAsync(_propsPath, baseline, _propsGenerator);

        baseline["Alpha"] = ["6.6.6"];
        await transaction.ApplyAsync([]);

        (await File.ReadAllTextAsync(_propsPath)).Should().Contain("1.0.0");
    }

    private Task<PropsUpdateTransaction> BeginAsync(params (string Name, string Version)[] packages)
    {
        var baseline = packages.ToDictionary(
            p => p.Name,
            p => new HashSet<string> { p.Version },
            StringComparer.OrdinalIgnoreCase);
        return PropsUpdateTransaction.BeginAsync(_propsPath, baseline, _propsGenerator);
    }

    private void WriteProps(params (string Name, string Version)[] packages)
    {
        var items = string.Join(
            Environment.NewLine,
            packages.Select(p => $"    <PackageVersion Include=\"{p.Name}\" Version=\"{p.Version}\" />"));

        File.WriteAllText(_propsPath,
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
            {items}
              </ItemGroup>
            </Project>
            """);
    }

    private static PackageUpdateEntry Update(string name, string current, string latest) =>
        new(name, current, latest, false, true);
}
