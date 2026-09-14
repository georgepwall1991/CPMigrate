namespace CPMigrate.Licensing;

/// <summary>
/// License metadata taken from a <c>.nuspec</c>. <see cref="LicenseType"/> is
/// <c>expression</c>, <c>file</c>, <c>url</c>, or <c>missing</c>.
/// </summary>
/// <param name="DevelopmentDependency">
/// The <c>developmentDependency</c> attribute on the nuspec's <c>&lt;metadata&gt;</c> element —
/// the package's own declaration that it only contributes at build time. It rides along here
/// because the reader has already parsed the document; it is not license data.
/// </param>
public sealed record NuspecLicense(
    string? Expression,
    string LicenseType,
    string? LicenseUrl,
    bool DevelopmentDependency = false
);
