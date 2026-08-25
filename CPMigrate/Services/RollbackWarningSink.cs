using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Emits rollback guidance on stderr — the one channel guaranteed to survive quiet mode and
/// machine-readable formats, whose consoles silence everything else.
/// </summary>
public static class RollbackWarningSink
{
    public static void Write(IReadOnlyList<string>? warnings)
    {
        if (warnings is not { Count: > 0 })
        {
            return;
        }

        foreach (var warning in warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }
}
