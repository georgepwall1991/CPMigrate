using CPMigrate.Fixers;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

public class MigrationRuntimeTests
{
    [Fact]
    public void Constructor_WiresRuntimeDependenciesAndCaseInsensitiveCache()
    {
        var console = new FakeConsoleService();
        var projectAnalyzer = new Mock<IProjectAnalyzer>().Object;
        var versionResolver = new VersionResolver(console);
        var propsGenerator = new PropsGenerator(versionResolver);
        var backupManager = new Mock<IBackupManager>().Object;
        var analysisService = new Mock<IAnalysisService>().Object;
        var fixService = new Mock<IFixService>().Object;
        var validator = new MigrationValidator(console);
        var display = new MigrationDisplay(console);

        var runtime = new MigrationRuntime(
            projectAnalyzer,
            versionResolver,
            propsGenerator,
            backupManager,
            console,
            analysisService,
            fixService,
            validator,
            display,
            quietMode: true);

        runtime.ProjectAnalyzer.Should().Be(projectAnalyzer);
        runtime.VersionResolver.Should().Be(versionResolver);
        runtime.PropsGenerator.Should().Be(propsGenerator);
        runtime.BackupManager.Should().Be(backupManager);
        runtime.ConsoleService.Should().Be(console);
        runtime.AnalysisService.Should().Be(analysisService);
        runtime.FixService.Should().Be(fixService);
        runtime.Validator.Should().Be(validator);
        runtime.Display.Should().Be(display);
        runtime.QuietMode.Should().BeTrue();
        runtime.ProgressReporter.Should().BeOfType<MigrationProgressReporter>();
        runtime.BackupCoordinator.Should().NotBeNull();

        runtime.CachedProjectScans["Project.csproj"] = [];
        runtime.CachedProjectScans.ContainsKey("project.csproj").Should().BeTrue();
    }
}
