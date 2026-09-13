using CPMigrate.Models;
using NuGet.Versioning;

namespace CPMigrate.Services;

public interface IUpdateService
{
    Task<NuGetVersion?> CheckForUpdatesAsync();
    Task<SelfUpdateResult> PerformUpdateAsync(bool force = false, bool dryRun = false);
}
