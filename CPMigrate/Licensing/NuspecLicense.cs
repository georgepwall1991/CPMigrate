namespace CPMigrate.Licensing;

/// <summary>
/// License metadata taken from a <c>.nuspec</c>. <see cref="LicenseType"/> is
/// <c>expression</c>, <c>file</c>, <c>url</c>, or <c>missing</c>.
/// </summary>
public sealed record NuspecLicense(string? Expression, string LicenseType, string? LicenseUrl);
