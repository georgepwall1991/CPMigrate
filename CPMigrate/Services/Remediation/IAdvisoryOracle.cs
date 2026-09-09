namespace CPMigrate.Services.Remediation;

/// <summary>
/// Answers which versions a known advisory applies to.
///
/// Named an oracle rather than a scanner on purpose: it is never asked what is wrong with a
/// solution. CPMigrate's vulnerability findings come from <c>dotnet list package --vulnerable</c> and
/// stay that way, so adding this source cannot invent a finding, contradict the SDK, or make a clean
/// solution report dirty. It is consulted only after the SDK has already named an advisory, to turn
/// "you are exposed" into "move to this version".
/// </summary>
public interface IAdvisoryOracle : IDisposable
{
    /// <summary>
    /// Advisories that could not be looked up this run, after retries.
    ///
    /// Reported for the same reason <see cref="INuGetVersionLookupService.GetFailedLookups"/> is: a
    /// null answer means "no data" and an empty range list means "affects nothing", and silently
    /// treating the first as the second would compute a fix version from an advisory nobody managed
    /// to read.
    /// </summary>
    /// <returns>Advisory identifiers that could not be resolved.</returns>
    IReadOnlyCollection<string> GetFailedLookups();

    /// <summary>
    /// Looks up one advisory.
    /// </summary>
    /// <param name="advisoryId">
    /// The advisory identifier, or the advisory URL the SDK reports — both are accepted, since the
    /// URL is the only form <c>dotnet list package --vulnerable</c> emits.
    /// </param>
    /// <param name="packageId">
    /// The package the caller is asking about. An advisory can cover several ecosystems and several
    /// packages; only the entries matching this NuGet package are kept.
    /// </param>
    /// <returns>
    /// The advisory, or null when it is unknown to the database or could not be fetched. The two are
    /// told apart through <see cref="GetFailedLookups"/>.
    /// </returns>
    Task<AdvisoryRecord?> LookupAsync(string advisoryId, string packageId);
}
