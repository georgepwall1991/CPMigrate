namespace CPMigrate.Services;

/// <summary>
/// Service for running the interactive wizard mode.
/// Guides users through options using arrow-key navigation prompts.
/// </summary>
public interface IInteractiveService
{
    /// <summary>
    /// Runs the interactive wizard and returns populated Options.
    /// </summary>
    /// <returns>Options configured through user prompts, or null if cancelled.</returns>
    Options? RunWizard();
}
