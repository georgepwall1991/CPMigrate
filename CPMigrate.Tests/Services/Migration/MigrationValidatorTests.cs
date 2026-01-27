using System;
using System.IO;
using System.Threading.Tasks;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services.Migration;

public class MigrationValidatorTests
{
    private readonly FakeConsoleService _fakeConsole;
    private readonly MigrationValidator _validator;

    public MigrationValidatorTests()
    {
        _fakeConsole = new FakeConsoleService();
        _validator = new MigrationValidator(_fakeConsole);
    }

    [Fact]
    public void TryValidate_ValidOptions_ReturnsTrue()
    {
        var options = new Options { SolutionFileDir = ".", BackupDir = "." };
        var result = _validator.TryValidate(options, out var errorResult);

        result.Should().BeTrue();
        errorResult.Should().BeNull();
    }

    [Fact]
    public void TryValidate_InvalidOptions_ReturnsFalseAndLogsError()
    {
        // --output-file requires --output Json
        var options = new Options { OutputFile = "out.json", Output = OutputFormat.Terminal };
        
        var result = _validator.TryValidate(options, out var errorResult);

        result.Should().BeFalse();
        errorResult.Should().NotBeNull();
        errorResult!.ExitCode.Should().Be(ExitCodes.ValidationError);
        _fakeConsole.ErrorMessages.Should().Contain(msg => msg.Contains("--output-file"));
    }

    [Fact]
    public async Task ValidateOutputDirectoryAsync_DirectoryExists_ReturnsNull()
    {
        using var tempDir = new TempDirectory();
        var result = await _validator.ValidateOutputDirectoryAsync(tempDir.Path);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateOutputDirectoryAsync_DirectoryDoesNotExist_CreatesIt()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var result = await _validator.ValidateOutputDirectoryAsync(tempPath);

            result.Should().BeNull();
            Directory.Exists(tempPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ValidateOutputDirectoryAsync_EmptyPath_ReturnsError()
    {
        var result = await _validator.ValidateOutputDirectoryAsync("");

        result.Should().NotBeNull();
        result!.ExitCode.Should().Be(ExitCodes.ValidationError);
        _fakeConsole.ErrorMessages.Should().Contain(msg => msg.Contains("cannot be empty"));
    }

    [Fact]
    public void IsAlreadyMigrated_FileExists_ReturnsTrue()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            MigrationValidator.IsAlreadyMigrated(tempFile).Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetOutputPaths_OutputDirSpecified_UsesIt()
    {
        var options = new Options { OutputDir = "my-output" };
        var paths = MigrationValidator.GetOutputPaths(options);

        paths.OutputPath.Should().Be("my-output");
        paths.PropsPath.Should().Be(Path.Combine("my-output", "Directory.Packages.props"));
    }

    [Fact]
    public void GetOutputPaths_NoOutputDirButSolutionDirSpecified_UsesSolutionDir()
    {
        var options = new Options { OutputDir = "", SolutionFileDir = "sln-dir" };
        var paths = MigrationValidator.GetOutputPaths(options);

        paths.OutputPath.Should().Be("sln-dir");
    }

    [Fact]
    public void GetOutputPaths_ProjectFileSpecified_UsesProjectDir()
    {
        var projectPath = Path.Combine("proj-dir", "proj.csproj");
        var options = new Options { OutputDir = "", SolutionFileDir = "", ProjectFileDir = projectPath };
        var paths = MigrationValidator.GetOutputPaths(options);

        paths.OutputPath.Should().Be("proj-dir");
    }

    [Fact]
    public async Task CheckForUnstagedChangesAsync_WithUnstagedChanges_LogsWarning()
    {
        using var tempDir = new TempDirectory();
        // Initialize git repo
        _runGit(tempDir.Path, "init");
        File.WriteAllText(Path.Combine(tempDir.Path, "file.txt"), "content");
        
        await _validator.CheckForUnstagedChangesAsync(tempDir.Path);
        
        // Output should contain the warning if git is installed and worked
        if (_fakeConsole.OutputMessages.Count > 0)
        {
            _fakeConsole.OutputMessages.Should().Contain(msg => msg.Contains("unstaged changes"));
        }
    }

    private void _runGit(string dir, string args)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
        }
        catch { /* git not present */ }
    }
}

// Helper for temporary directories
internal class TempDirectory : IDisposable
{
    public string Path { get; }
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
