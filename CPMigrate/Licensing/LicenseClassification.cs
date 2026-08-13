namespace CPMigrate.Licensing;

/// <summary>
/// How risky a license is for distribution. Ordered from most permissive to most restrictive so
/// SPDX <c>OR</c> can take the minimum and <c>AND</c> the maximum.
/// </summary>
public enum LicenseClassification
{
    Permissive = 0,
    Unknown = 1,
    WeakCopyleft = 2,
    Proprietary = 3,
    StrongCopyleft = 4,
}
