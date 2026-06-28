using CPMigrate.Models;

namespace CPMigrate.Services.Migration;

/// <summary>
/// Validates migration options and environment before executing migrations.
/// </summary>
internal class MigrationValidator
{
    private readonly IConsoleService _consoleService;

    public MigrationValidator(IConsoleService consoleService)
    {
        _consoleService = consoleService;
    }

    /// <summary>
    /// Validates options and returns error result if validation fails.
    /// </summary>
    public bool TryValidate(Options options, out MigrationResult? errorResult)
    {
        try
        {
            options.Validate();
            errorResult = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            _consoleService.Error(ex.Message);
            errorResult = new MigrationResult { ExitCode = ExitCodes.ValidationError };
            return false;
        }
    }

    /// <summary>
    /// Validates that the output directory exists or can be created.
    /// </summary>
    public MigrationResult? ValidateOutputDirectory(string outputPath)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            _consoleService.Error("Output directory cannot be empty.");
            return new MigrationResult { ExitCode = ExitCodes.ValidationError };
        }

        try
        {
            if (!Directory.Exists(outputPath))
            {
                _consoleService.Warning($"Output directory does not exist: {outputPath}");
                _consoleService.Info("Creating output directory...");
                Directory.CreateDirectory(outputPath);
            }

            return null; // No error
        }
        catch (Exception ex)
        {
            _consoleService.Error($"Failed to create output directory: {ex.Message}");
            return new MigrationResult { ExitCode = ExitCodes.FileOperationError };
        }
    }

    /// <summary>
    /// Checks for unstaged changes in git and warns the user.
    /// </summary>
    public async Task CheckForUnstagedChangesAsync(string directory)
    {
        if (!Directory.Exists(Path.Combine(directory, ".git")))
        {
            return; // Not a git repository
        }

        try
        {
            using var process = new System.Diagnostics.Process();
#pragma warning disable S4036 // Suppress PATH warning: CLI tool intentionally uses git from PATH
            process.StartInfo.FileName = "git";
#pragma warning restore S4036
            process.StartInfo.Arguments = "status --porcelain";
            process.StartInfo.WorkingDirectory = directory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(output))
            {
                _consoleService.WriteLine();
                _consoleService.Warning("⚠️  You have unstaged changes in your git repository.");
                _consoleService.Dim("   Consider committing or stashing them before migration.");
                _consoleService.WriteLine();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Git not available, not in PATH, or other expected error - silently continue
        }
    }

    /// <summary>
    /// Checks if the directory is already migrated to CPM.
    /// </summary>
    public static bool IsAlreadyMigrated(string propsPath) => File.Exists(propsPath);

    /// <summary>
    /// Gets the output paths for the migration.
    /// </summary>
    public static (string OutputPath, string PropsPath) GetOutputPaths(Options options)
    {
        string outputPath;

        // Prioritize OutputDir only if it's not the default or if nothing else is specified
        if (!string.IsNullOrEmpty(options.OutputDir) && options.OutputDir != ".")
        {
            outputPath = options.OutputDir;
        }
        else if (options.HasExplicitSolutionPath)
        {
            // SolutionFileDir may legitimately be a directory OR a .sln/.slnx file (per README
            // quickstart: `cpmigrate -s ./MySolution.sln`). When it's a solution file path, write
            // Directory.Packages.props into the directory containing the solution; otherwise
            // Directory.CreateDirectory would collide with the existing solution file (FileOperationError).
            if (IsSolutionFilePath(options.SolutionFileDir))
            {
                var parent = Path.GetDirectoryName(options.SolutionFileDir);
                outputPath = string.IsNullOrWhiteSpace(parent) ? "." : parent;
            }
            else
            {
                outputPath = options.SolutionFileDir;
            }
        }
        else if (options.HasExplicitProjectPath)
        {
            outputPath = Path.GetDirectoryName(options.ProjectFileDir) ?? ".";
        }
        else
        {
            // Fallback to whatever is in OutputDir (likely ".")
            outputPath = options.OutputDir ?? ".";
        }

        var propsPath = Path.Combine(outputPath, "Directory.Packages.props");
        return (outputPath, propsPath);
    }

    private static bool IsSolutionFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase);
    }
}
