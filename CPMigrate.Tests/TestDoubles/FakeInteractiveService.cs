using CPMigrate.Services;

namespace CPMigrate.Tests.TestDoubles;

/// <summary>
/// Fake implementation of IInteractiveService for testing.
/// </summary>
public class FakeInteractiveService : IInteractiveService
{
    /// <summary>
    /// The next result to return from RunWizard.
    /// </summary>
    public Options? NextWizardResult { get; set; }

    public Options? RunWizard()
    {
        return NextWizardResult;
    }
}
