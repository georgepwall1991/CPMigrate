namespace CPMigrate.Licensing;

/// <summary>
/// A parsed NuGet/SPDX license expression. Operators are explicit in the tree so AND and OR cannot
/// be confused by a later classifier.
/// </summary>
#pragma warning disable S2094 // Abstract union root — members live on the derived records.
public abstract record LicenseExpression;
#pragma warning restore S2094

/// <summary>A single SPDX identifier, preserving the casing from the source text.</summary>
public sealed record LicenseIdentifier(string Id) : LicenseExpression;

/// <summary>Both sides must be complied with.</summary>
public sealed record LicenseAnd(LicenseExpression Left, LicenseExpression Right) : LicenseExpression;

/// <summary>Either side may be chosen.</summary>
public sealed record LicenseOr(LicenseExpression Left, LicenseExpression Right) : LicenseExpression;

/// <summary>A license with an SPDX exception (<c>WITH</c>).</summary>
public sealed record LicenseWith(LicenseExpression License, string Exception) : LicenseExpression;
