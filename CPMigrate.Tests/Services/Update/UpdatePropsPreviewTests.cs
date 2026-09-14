using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Update;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Update;

public class UpdatePropsPreviewTests : IDisposable
{
    private readonly string _directory;
    private readonly string _propsPath;
    private readonly PropsGenerator _propsGenerator = new();

    public UpdatePropsPreviewTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"CPMigratePreview_{Guid.NewGuid():N}");
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
    public void PlannedContent_RendersTheWritePathsOutput()
    {
        WriteProps(("Alpha", "1.0.0"), ("Beta", "2.0.0"));
        var current = Baseline(("Alpha", "1.0.0"), ("Beta", "2.0.0"));

        var planned = UpdatePropsPreview.PlannedContent(
            _propsGenerator,
            _propsPath,
            current,
            [Update("Alpha", "1.0.0", "1.5.0")]);

        planned.Should().NotBeNull();
        planned.Should().Contain("1.5.0");
        planned.Should().Contain("2.0.0", "untouched pins keep their baseline version");
    }

    [Fact]
    public void PlannedContent_MalformedProps_ReturnsNullInsteadOfThrowing()
    {
        // The preview is a courtesy over the same machinery the write uses; a props file MSBuild
        // cannot parse is the real run's failure to report, not the dry run's to crash on.
        File.WriteAllText(_propsPath, "<Project><ItemGroup><PackageVersion Include=");
        var current = Baseline(("Alpha", "1.0.0"));

        var planned = UpdatePropsPreview.PlannedContent(
            _propsGenerator,
            _propsPath,
            current,
            [Update("Alpha", "1.0.0", "1.5.0")]);

        planned.Should().BeNull();
    }

    [Fact]
    public void PlannedContent_MissingProps_ReturnsNull()
    {
        var current = Baseline(("Alpha", "1.0.0"));

        var planned = UpdatePropsPreview.PlannedContent(
            _propsGenerator,
            _propsPath,
            current,
            [Update("Alpha", "1.0.0", "1.5.0")]);

        planned.Should().BeNull();
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

    private static Dictionary<string, HashSet<string>> Baseline(params (string Name, string Version)[] packages) =>
        packages.ToDictionary(
            p => p.Name,
            p => new HashSet<string> { p.Version },
            StringComparer.OrdinalIgnoreCase);

    private static PackageUpdateEntry Update(string name, string current, string latest) =>
        new(name, current, latest, false, true);
}
