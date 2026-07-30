using CPMigrate.Fixers;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using Spectre.Console;

namespace CPMigrate.Tests.Services;

public class MigrationServiceCoverageTests
{
    private readonly Mock<IConsoleService> _mockConsole;
    private readonly Mock<IProjectAnalyzer> _mockAnalyzer;
    private readonly Mock<IAnalysisService> _mockAnalysis;
    private readonly Mock<IFixService> _mockFix;
    private readonly BackupManager _backupManager;
    private readonly MigrationService _service;

    public MigrationServiceCoverageTests()
    {
        _mockConsole = new Mock<IConsoleService>();
        // These tests drive the prompt-bearing paths, so the console must look like a TTY.
        _mockConsole.SetupGet(c => c.IsInteractive).Returns(true);
        _mockAnalyzer = new Mock<IProjectAnalyzer>();

        // The declaration scan feeds the rules that read the project file rather than the
        // resolved graph. Unstubbed it answers "could not read", which is now counted as
        // incomplete coverage.
        _mockAnalyzer
            .Setup(a => a.ScanDeclaredPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), true));
        _mockAnalysis = new Mock<IAnalysisService>();
        _mockFix = new Mock<IFixService>();
        _backupManager = new BackupManager();

        _service = new MigrationService(
            _mockConsole.Object,
            _mockAnalyzer.Object,
            new VersionResolver(_mockConsole.Object),
            null, // propsGenerator
            _backupManager,
            _mockAnalysis.Object,
            _mockFix.Object);
    }

    [Fact]
    public async Task ExecuteRollbackAsync_MissingDirectory_ReturnsError()
    {
        // Arrange
        var options = new Options { Rollback = true, BackupDir = "non_existent_folder_abc_123" };

        // Act
        var result = await _service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.FileOperationError);
        _mockConsole.Verify(c => c.Error(It.Is<string>(s => s.Contains("No backup directory found"))), Times.Once);
    }

    [Fact]
    public async Task ExecuteRollbackAsync_MissingManifest_ReturnsError()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var backupDir = Path.Combine(tempDir, ".cpmigrate_backup"); // Account for BackupManager logic
        Directory.CreateDirectory(backupDir);
        var options = new Options { Rollback = true, BackupDir = tempDir };

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.FileOperationError);
            _mockConsole.Verify(c => c.Error(It.Is<string>(s => s.Contains("No backup manifest found"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_HighFailureRate_ShowsWarning()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "P1.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(tempDir, "P2.csproj"), "<Project />");

        var options = new Options { Analyze = true, SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { Path.Combine(tempDir, "P1.csproj"), Path.Combine(tempDir, "P2.csproj") }));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), false)); // Always fail

        _mockAnalysis.Setup(a => a.Analyze(It.IsAny<ProjectPackageInfo>()))
            .Returns(new AnalysisReport(2, 0, new List<AnalyzerResult>()));

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            _mockConsole.Verify(c => c.Warning(It.Is<string>(s => s.Contains("failed to scan"))), Times.AtLeastOnce);
            _mockConsole.Verify(c => c.Warning(It.Is<string>(s => s.Contains("High failure rate detected"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_ProjectMode_UsesProjectDiscovery()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, "<Project />");

        var options = new Options { Analyze = true, ProjectFileDir = projectPath };

        _mockAnalyzer.Setup(a => a.DiscoverProjectFromPath(projectPath))
            .Returns((tempDir, new List<string> { projectPath }));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(projectPath))
            .Returns((new List<PackageReference>(), true));

        _mockAnalysis.Setup(a => a.Analyze(It.IsAny<ProjectPackageInfo>()))
            .Returns(new AnalysisReport(1, 0, new List<AnalyzerResult>()));

        try
        {
            var result = await _service.ExecuteAsync(options);

            result.ExitCode.Should().Be(ExitCodes.Success);
            _mockAnalyzer.Verify(a => a.DiscoverProjectFromPath(projectPath), Times.Once);
            _mockAnalyzer.Verify(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_IncludeTransitive_WhenResolvedScanFails_DoesNotFallbackToProjectXml()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, "<Project />");

        var options = new Options { Analyze = true, IncludeTransitive = true, SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        _mockAnalyzer.Setup(a => a.ScanResolvedPackagesAsync(projectPath, true))
            .ReturnsAsync((new List<PackageReference>(), false));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference> { new("Fallback.Package", "1.0.0", projectPath, "P1.csproj") }, true));

        _mockAnalysis.Setup(a => a.Analyze(It.Is<ProjectPackageInfo>(info => info.TotalReferences == 0)))
            .Returns(new AnalysisReport(0, 0, new List<AnalyzerResult>()));

        try
        {
            // Act
            await _service.ExecuteAsync(options);

            // Assert
            _mockAnalyzer.Verify(a => a.ScanProjectPackages(projectPath), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_WithVulnerabilities_ReportsCorrectly()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "P1.csproj"), "<Project />");

        var options = new Options { Analyze = true, AuditSecurity = true, SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { Path.Combine(tempDir, "P1.csproj") }));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), true));

        _mockAnalyzer.Setup(a => a.ScanVulnerabilitiesAsync(It.IsAny<string>()))
            .ReturnsAsync((new List<VulnerabilityInfo> { new VulnerabilityInfo("Pkg", "High", "CVE-123", "1.0", "2.0", "P1") }, true));

        _mockAnalysis.Setup(a => a.Analyze(It.IsAny<ProjectPackageInfo>()))
            .Returns(new AnalysisReport(1, 0, new List<AnalyzerResult>()));

        try
        {
            // Act
            await _service.ExecuteAsync(options);

            // Assert
            _mockConsole.Verify(c => c.WriteAnalysisHeader(It.IsAny<int>(), It.IsAny<int>(), 1), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_NoProjectsFound_ReturnsNoProjectsFound()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var options = new Options { SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string>()));

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.NoProjectsFound);
            _mockConsole.Verify(c => c.Error(It.Is<string>(s => s.Contains("No projects found"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ConflictFound_FailStrategy_TriggersRollbackPrompt()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        // Provide real XML with conflicts to ensure they are picked up by static ProjectAnalyzer.ProcessProject
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg"" Version=""1.0"" />
    <PackageReference Include=""Pkg"" Version=""2.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir, ConflictStrategy = ConflictStrategy.Fail };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        _mockConsole.Setup(c => c.AskConfirmation(It.IsAny<string>())).Returns(true);

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.VersionConflict);
            _mockConsole.Verify(c => c.Warning(It.Is<string>(s => s.Contains("Migration interrupted"))), Times.Once);
            _mockConsole.Verify(c => c.AskConfirmation(It.Is<string>(s => s.Contains("Would you like to rollback"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_TransitivePackages_CallsScanTransitive()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, "<Project />");

        var options = new Options { SolutionFileDir = tempDir, IncludeTransitive = true };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(It.IsAny<string>(), It.IsAny<Dictionary<string, HashSet<string>>>()))
            .Returns(true);

        _mockAnalyzer.Setup(a => a.ScanTransitivePackagesAsync(It.IsAny<string>()))
            .ReturnsAsync((new List<PackageReference>(), true));

        try
        {
            // Act
            await _service.ExecuteAsync(options);

            // Assert
            _mockAnalyzer.Verify(a => a.ScanTransitivePackagesAsync(projectPath), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_AlreadyMigrated_NoMerge_ReturnsAlreadyMigrated()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Directory.Packages.props"), "<Project />");

        var options = new Options { SolutionFileDir = tempDir, MergeExisting = false };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { Path.Combine(tempDir, "P1.csproj") }));

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success); // Display returns success code for "Already Migrated"
            _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("already migrated to CPM"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_WithFix_CallsFixService()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "P1.csproj"), "<Project />");

        var options = new Options { Analyze = true, Fix = true, SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { Path.Combine(tempDir, "P1.csproj") }));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>(), true));

        var report = new AnalysisReport(1, 0, new List<AnalyzerResult>
        {
            new AnalyzerResult("Test", new List<AnalysisIssue> { new AnalysisIssue("Pkg", "Issue", new List<string> { "P1" }) })
        });
        _mockAnalysis.Setup(a => a.Analyze(It.IsAny<ProjectPackageInfo>()))
            .Returns(report);

        _mockFix.Setup(f => f.ApplyFixes(It.IsAny<AnalysisReport>(), It.IsAny<ProjectPackageInfo>(), It.IsAny<Options>(), It.IsAny<bool>()))
            .Returns(new FixReport { Results = { FixResult.Succeeded("Fixed", new List<FileChange>()) } });

        try
        {
            // Act
            await _service.ExecuteAsync(options);

            // Assert
            _mockFix.Verify(f => f.ApplyFixes(report, It.IsAny<ProjectPackageInfo>(), options, false), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteRollbackAsync_NonInteractiveConsole_DeclinesInsteadOfPrompting()
    {
        // A redirected stdout (CI, `| tee`) cannot service a selection prompt. The rollback must
        // decline rather than let Spectre throw "Cannot show selection prompt", and must not
        // touch the file it never got confirmation to restore.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projectDir = Path.Combine(tempDir, "Project");
        var backupDir = Path.Combine(tempDir, ".cpmigrate_backup");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(backupDir);

        var projectPath = Path.Combine(projectDir, "P1.csproj");
        File.WriteAllText(projectPath, "current content");

        var backupFileName = "P1.csproj.backup_123";
        File.WriteAllText(Path.Combine(backupDir, backupFileName), "original content");

        await BackupManager.WriteManifestAsync(backupDir, new BackupManifest
        {
            Backups = new List<BackupEntry>
            {
                new BackupEntry { OriginalPath = projectPath, BackupFileName = backupFileName }
            },
            PropsFilePath = Path.Combine(projectDir, "Directory.Packages.props"),
            PropsFileExisted = false
        });

        _mockConsole.SetupGet(c => c.IsInteractive).Returns(false);

        try
        {
            await _service.ExecuteAsync(new Options { Rollback = true, BackupDir = tempDir });

            _mockConsole.Verify(c => c.AskConfirmation(It.IsAny<string>()), Times.Never);
            File.ReadAllText(projectPath).Should().Be("current content");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteRollbackAsync_Success_RestoresFiles()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projectDir = Path.Combine(tempDir, "Project");
        var backupDir = Path.Combine(tempDir, ".cpmigrate_backup");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(backupDir);

        var projectPath = Path.Combine(projectDir, "P1.csproj");
        File.WriteAllText(projectPath, "corrupted content");

        var backupFileName = "P1.csproj.backup_123";
        var backupFilePath = Path.Combine(backupDir, backupFileName);
        File.WriteAllText(backupFilePath, "original content");

        var manifest = new BackupManifest
        {
            Backups = new List<BackupEntry>
            {
                new BackupEntry { OriginalPath = projectPath, BackupFileName = backupFileName }
            },
            PropsFilePath = Path.Combine(projectDir, "Directory.Packages.props"),
            PropsFileExisted = false
        };
        await BackupManager.WriteManifestAsync(backupDir, manifest);

        var options = new Options { Rollback = true, BackupDir = tempDir };

        _mockConsole.Setup(c => c.AskConfirmation(It.IsAny<string>())).Returns(true);

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            File.ReadAllText(projectPath).Should().Be("original content");
            _mockConsole.Verify(c => c.Success(It.Is<string>(s => s.Contains("Rollback complete"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_Success_CreatesPropsFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg1"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            result.ProjectsProcessed.Should().Be(1);
            result.PackagesCentralized.Should().Be(1);
            File.Exists(result.PropsFilePath).Should().BeTrue();
            File.ReadAllText(projectPath).Should().NotContain("Version=\"1.0.0\"");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_MergeExisting_Success()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var propsPath = Path.Combine(tempDir, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""Pkg1"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg2"" Version=""2.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir, MergeExisting = true };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            result.PackagesCentralized.Should().Be(2); // Pkg1 (existing) + Pkg2 (new)
            _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("Loaded 1 package"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ConflictResolvedWithHighest_Success()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        // Provide real XML with conflicts
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg"" Version=""1.0.0"" />
    <PackageReference Include=""Pkg"" Version=""2.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir, ConflictStrategy = ConflictStrategy.Highest };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            result.ConflictsResolved.Should().Be(1);
            File.ReadAllText(result.PropsFilePath).Should().Contain("Version=\"2.0.0\"");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_DryRun_DoesNotModifyFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        var originalContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg1"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, originalContent);

        var options = new Options { SolutionFileDir = tempDir, DryRun = true };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            File.ReadAllText(projectPath).Should().Be(originalContent);
            _mockConsole.Verify(c => c.DryRun(It.Is<string>(s => s.Contains("DRY RUN"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_NoBackup_SkipsBackup()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg1"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir, NoBackup = true };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            // Act
            await _service.ExecuteAsync(options);

            // Assert
            var backupDir = Path.Combine(tempDir, ".cpmigrate_backup");
            Directory.Exists(backupDir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ConflictFound_InteractiveConflicts_AsksSelection()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg"" Version=""1.0.0"" />
    <PackageReference Include=""Pkg"" Version=""2.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir, InteractiveConflicts = true };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference> { new PackageReference("Pkg", "1.0.0", projectPath, "P1.csproj") }, true));

        _mockConsole.Setup(c => c.AskSelection(It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
            .Returns("1.0.0 (Used by 1 project)");

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            result.ConflictsResolved.Should().Be(1);
            File.ReadAllText(result.PropsFilePath).Should().Contain("Version=\"1.0.0\"");
            _mockConsole.Verify(c => c.AskSelection(It.Is<string>(s => s.Contains("Version for Pkg")), It.IsAny<IEnumerable<string>>()), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ProjectMode_UsesProjectDiscovery()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options
        {
            ProjectFileDir = projectPath,
            OutputDir = tempDir,
            NoBackup = true,
            DryRun = true
        };

        _mockAnalyzer.Setup(a => a.DiscoverProjectFromPath(projectPath))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            var result = await _service.ExecuteAsync(options);

            result.ExitCode.Should().Be(ExitCodes.Success);
            _mockAnalyzer.Verify(a => a.DiscoverProjectFromPath(projectPath), Times.Once);
            _mockAnalyzer.Verify(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ProjectModeInteractiveConflicts_DoesNotRescanSolution()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg"" Version=""1.0.0"" />
    <PackageReference Include=""Pkg"" Version=""2.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options
        {
            ProjectFileDir = projectPath,
            InteractiveConflicts = true,
            OutputDir = tempDir,
            NoBackup = true
        };

        _mockAnalyzer.Setup(a => a.DiscoverProjectFromPath(projectPath))
            .Returns((tempDir, new List<string> { projectPath }));

        _mockAnalyzer.Setup(a => a.ScanProjectPackages(It.IsAny<string>()))
            .Returns((new List<PackageReference>
            {
                new("Pkg", "1.0.0", projectPath, "P1.csproj"),
                new("Pkg", "2.0.0", projectPath, "P1.csproj")
            }, true));

        _mockConsole.Setup(c => c.AskSelection(It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
            .Returns("2.0.0 (Used by 1 project)");

        try
        {
            var result = await _service.ExecuteAsync(options);

            result.ExitCode.Should().Be(ExitCodes.Success);
            _mockAnalyzer.Verify(a => a.DiscoverProjectFromPath(projectPath), Times.AtLeastOnce);
            _mockAnalyzer.Verify(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteListBackupsAsync_NoBackups_ShowsInfo()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var options = new Options { ListBackups = true, BackupDir = tempDir };

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("No backups found"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_ExceptionAfterBackup_TriggersRollbackPrompt()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg"" Version=""1.0.0"" />
    <PackageReference Include=""Pkg"" Version=""2.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        // Force exception during conflict table writing (after backups)
        _mockConsole.Setup(c => c.WriteConflictsTable(It.IsAny<Dictionary<string, HashSet<string>>>(), It.IsAny<List<string>>(), It.IsAny<ConflictStrategy>()))
            .Throws(new Exception("Mock error after backup"));

        try
        {
            // Act (will throw)
            await _service.ExecuteAsync(options);
        }
        catch (Exception)
        {
            // Expected
        }

        // Assert
        _mockConsole.Verify(c => c.Warning(It.Is<string>(s => s.Contains("partially modified"))), Times.Once);
        _mockConsole.Verify(c => c.AskConfirmation(It.Is<string>(s => s.Contains("rollback"))), Times.Once);

        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task ExecuteMigrationAsync_AddGitIgnore_CreatesFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg1"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir, AddBackupToGitignore = true, GitignoreDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            // Act
            await _service.ExecuteAsync(options);

            // Assert
            var gitignorePath = Path.Combine(tempDir, ".gitignore");
            File.Exists(gitignorePath).Should().BeTrue();
            File.ReadAllText(gitignorePath).Should().Contain(".cpmigrate_backup");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteRollbackAsync_UserDeclines_ReturnsSuccess()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var backupDir = Path.Combine(tempDir, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);

        var manifest = new BackupManifest
        {
            Backups = new List<BackupEntry> { new BackupEntry { OriginalPath = "P1.csproj", BackupFileName = "P1.csproj.bak" } },
            PropsFilePath = Path.Combine(tempDir, "Directory.Packages.props")
        };
        await BackupManager.WriteManifestAsync(backupDir, manifest);

        var options = new Options { Rollback = true, BackupDir = tempDir };
        _mockConsole.Setup(c => c.AskConfirmation(It.IsAny<string>())).Returns(false);

        try
        {
            // Act
            var result = await _service.ExecuteAsync(options);

            // Assert
            result.ExitCode.Should().Be(ExitCodes.Success);
            _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("cancelled"))), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_QuietService_SuppressesOutput()
    {
        // Arrange
        var quietService = new MigrationService(
            _mockConsole.Object,
            projectAnalyzer: _mockAnalyzer.Object,
            analysisService: _mockAnalysis.Object,
            fixService: _mockFix.Object,
            backupManager: _backupManager,
            quietMode: true);

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "P1.csproj");
        File.WriteAllText(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Pkg1"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var options = new Options { SolutionFileDir = tempDir };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { projectPath }));

        try
        {
            // Act
            await quietService.ExecuteAsync(options);

            // Assert
            _mockConsole.Verify(c => c.Info(It.IsAny<string>()), Times.Never);
            _mockConsole.Verify(c => c.Success(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteMigrationAsync_MalformedPropsFile_ReturnsFileOperationError()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var propsPath = Path.Combine(tempDir, "Directory.Packages.props");
        // Malformed XML that will cause ProjectRootElement to fail
        File.WriteAllText(propsPath, "<Project> <Untouched </Project>");

        var options = new Options { SolutionFileDir = tempDir, MergeExisting = true };

        _mockAnalyzer.Setup(a => a.DiscoverProjectsFromSolution(It.IsAny<string>()))
            .Returns((tempDir, new List<string> { "P1.csproj" }));

        // Act
        var result = await _service.ExecuteAsync(options);

        // Assert
        result.ExitCode.Should().Be(ExitCodes.FileOperationError);
        _mockConsole.Verify(c => c.Error(It.Is<string>(s => s.Contains("Failed to read existing"))), Times.Once);

        Directory.Delete(tempDir, true);
    }

}
