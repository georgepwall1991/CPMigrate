using NuGet.Versioning;

namespace CPMigrate.Services;

public interface IUpdateService
{
    Task<NuGetVersion?> CheckForUpdatesAsync();
    Task<bool> PerformUpdateAsync();
}
