using CommandLine;
using CPMigrate;
using CPMigrate.Services;

return await Parser.Default.ParseArguments<Options>(args)
    .MapResult(
        async opt => await RunMigration(opt),
        _ => Task.FromResult(ExitCodes.ValidationError));

static async Task<int> RunMigration(Options opt)
{
    try
    {
        var migrationService = new MigrationService();
        var result = await migrationService.ExecuteAsync(opt);
        return result.ExitCode;
    }
    catch (IOException ex)
    {
        ConsoleOutput.Error($"\nFile operation error: {ex.Message}");
        Console.Error.WriteLine("\nSuggestion: Check file permissions and ensure no files are locked by another process.");
        return ExitCodes.FileOperationError;
    }
    catch (UnauthorizedAccessException ex)
    {
        ConsoleOutput.Error($"\nPermission denied: {ex.Message}");
        Console.Error.WriteLine("\nSuggestion: Run with elevated permissions or check file/folder access rights.");
        return ExitCodes.FileOperationError;
    }
    catch (Exception ex)
    {
        ConsoleOutput.Error($"\nUnexpected error: {ex.Message}");
#if DEBUG
        Console.Error.WriteLine(ex.StackTrace);
#endif
        return ExitCodes.FileOperationError;
    }
}
