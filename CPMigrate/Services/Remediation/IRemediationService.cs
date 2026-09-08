using CPMigrate.Models;

namespace CPMigrate.Services.Remediation;

/// <summary>
/// Clears known security advisories by moving each vulnerable package the smallest distance that
/// escapes them, then proving the result with the project's own tests.
/// </summary>
public interface IRemediationService
{
    /// <summary>
    /// Plans and, unless this is a dry run, applies and verifies a remediation.
    /// </summary>
    /// <param name="request">What to remediate and how.</param>
    /// <returns>The receipt, including a post-remediation re-scan when anything was applied.</returns>
    Task<RemediationResult> RemediateAsync(RemediateRequest request);
}
