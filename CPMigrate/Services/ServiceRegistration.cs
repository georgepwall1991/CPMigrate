using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CPMigrate.Services;

/// <summary>
/// Registers all application services into the DI container.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Builds a fully configured <see cref="IServiceProvider"/> for the application.
    /// </summary>
    public static ServiceProvider BuildServiceProvider(
        IConsoleService consoleService,
        ILoggerFactory loggerFactory,
        bool quietMode = false)
    {
        var services = new ServiceCollection();

        // Logging
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Core services
        services.AddSingleton(consoleService);
        services.AddSingleton<IDotNetCliService, DotNetCliService>();
        services.AddSingleton<IProjectAnalyzer>(sp =>
            new ProjectAnalyzer(
                sp.GetRequiredService<IConsoleService>(),
                sp.GetRequiredService<IDotNetCliService>(),
                sp.GetRequiredService<ILogger<ProjectAnalyzer>>()));
        services.AddSingleton<IBackupManager, BackupManager>();
        services.AddSingleton(sp =>
            new VersionResolver(sp.GetRequiredService<IConsoleService>()));
        services.AddSingleton(sp =>
            new PropsGenerator(sp.GetRequiredService<VersionResolver>()));
        services.AddSingleton<IInteractiveService>(sp =>
            new InteractiveService(sp.GetRequiredService<IConsoleService>()));
        services.AddSingleton(sp =>
            new ConfigService(sp.GetRequiredService<IConsoleService>()));

        // Analysis services
        services.AddSingleton(sp =>
            new DependencyGraphService(sp.GetRequiredService<IConsoleService>()));
        services.AddSingleton<IAnalysisService>(sp =>
            new AnalysisService(
                null, // uses default analyzers
                sp.GetRequiredService<DependencyGraphService>(),
                sp.GetRequiredService<IProjectAnalyzer>()));
        services.AddSingleton<IFixService>(sp =>
            new FixService(
                sp.GetRequiredService<IConsoleService>(),
                sp.GetRequiredService<VersionResolver>()));

        // Migration service
        services.AddTransient(sp =>
            new MigrationService(
                sp.GetRequiredService<IConsoleService>(),
                sp.GetRequiredService<IProjectAnalyzer>(),
                sp.GetRequiredService<VersionResolver>(),
                sp.GetRequiredService<PropsGenerator>(),
                sp.GetRequiredService<IBackupManager>(),
                sp.GetRequiredService<IAnalysisService>(),
                sp.GetRequiredService<IFixService>(),
                quietMode,
                sp.GetRequiredService<ILogger<MigrationService>>()));

        // Update service
        services.AddTransient<IUpdateService>(sp =>
            new UpdateService(
                sp.GetRequiredService<IConsoleService>(),
                logger: sp.GetRequiredService<ILogger<UpdateService>>()));

        return services.BuildServiceProvider();
    }
}
