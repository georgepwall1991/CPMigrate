using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace CPMigrate.Services;

/// <summary>
/// Configures the logging pipeline for the application.
/// </summary>
public static class LoggingConfiguration
{
    private const string DefaultLogFileName = "cpmigrate.log";

    /// <summary>
    /// Creates an <see cref="ILoggerFactory"/> based on the verbosity setting.
    /// When verbose mode is enabled, logs to a file alongside console diagnostics.
    /// When disabled, returns a no-op logger factory to avoid overhead.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(bool verbose)
    {
        if (!verbose)
        {
            return LoggerFactory.Create(_ => { });
        }

        var logPath = Path.Combine(Directory.GetCurrentDirectory(), DefaultLogFileName);

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 3)
            .CreateLogger();

        return LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(serilogLogger, dispose: true);
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }
}
