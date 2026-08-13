namespace CPMigrate.Licensing;

/// <summary>
/// Maps SPDX identifiers onto a risk ranking. Package names do not belong here — the identifier
/// is the thing that is licensed, not the package id that happens to carry it today.
/// </summary>
public static class LicenseRiskClassifier
{
    private static readonly HashSet<string> Permissive = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT",
        "MIT-0",
        "Apache-1.0",
        "Apache-1.1",
        "Apache-2.0",
        "BSD-2-Clause",
        "BSD-3-Clause",
        "BSD-3-Clause-Clear",
        "ISC",
        "0BSD",
        "Unlicense",
        "CC0-1.0",
        "BlueOak-1.0.0",
        "PostgreSQL",
        "Python-2.0",
        "WTFPL",
        "Zlib",
        "BSL-1.0",
        "MS-PL",
        "NCSA",
    };

    private static readonly HashSet<string> WeakCopyleft = new(StringComparer.OrdinalIgnoreCase)
    {
        "LGPL-2.0",
        "LGPL-2.0-only",
        "LGPL-2.0-or-later",
        "LGPL-2.1",
        "LGPL-2.1-only",
        "LGPL-2.1-or-later",
        "LGPL-3.0",
        "LGPL-3.0-only",
        "LGPL-3.0-or-later",
        "MPL-1.0",
        "MPL-1.1",
        "MPL-2.0",
        "EPL-1.0",
        "EPL-2.0",
        "CDDL-1.0",
        "CDDL-1.1",
        "CPL-1.0",
        "MS-RL",
    };

    private static readonly HashSet<string> StrongCopyleft = new(StringComparer.OrdinalIgnoreCase)
    {
        "GPL-1.0",
        "GPL-1.0-only",
        "GPL-1.0-or-later",
        "GPL-1.0+",
        "GPL-2.0",
        "GPL-2.0-only",
        "GPL-2.0-or-later",
        "GPL-2.0+",
        "GPL-3.0",
        "GPL-3.0-only",
        "GPL-3.0-or-later",
        "GPL-3.0+",
        "AGPL-1.0",
        "AGPL-1.0-only",
        "AGPL-1.0-or-later",
        "AGPL-3.0",
        "AGPL-3.0-only",
        "AGPL-3.0-or-later",
        "EUPL-1.1",
        "EUPL-1.2",
        "SSPL-1.0",
    };

    private static readonly HashSet<string> Proprietary = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUSL-1.1",
        "Elastic-2.0",
        "CC-BY-NC-4.0",
        "CC-BY-NC-SA-4.0",
        "CC-BY-NC-ND-4.0",
        "CC-BY-NC-3.0",
        "Proprietary",
        "Commercial",
    };

    public static LicenseClassification ClassifyIdentifier(string spdxId)
    {
        // Stryker disable once block : empty/null ids miss every table and fall through to Unknown anyway
        if (string.IsNullOrEmpty(spdxId))
        {
            return LicenseClassification.Unknown;
        }

        if (Permissive.Contains(spdxId))
        {
            return LicenseClassification.Permissive;
        }

        if (WeakCopyleft.Contains(spdxId))
        {
            return LicenseClassification.WeakCopyleft;
        }

        if (StrongCopyleft.Contains(spdxId))
        {
            return LicenseClassification.StrongCopyleft;
        }

        if (Proprietary.Contains(spdxId))
        {
            return LicenseClassification.Proprietary;
        }

        return LicenseClassification.Unknown;
    }

    public static LicenseClassification Classify(LicenseExpression expression)
    {
        return expression switch
        {
            LicenseIdentifier identifier => ClassifyIdentifier(identifier.Id),
            LicenseOr or => Min(Classify(or.Left), Classify(or.Right)),
            LicenseAnd and => Max(Classify(and.Left), Classify(and.Right)),
            LicenseWith with => Classify(with.License),
            _ => LicenseClassification.Unknown,
        };
    }

    public static LicenseClassification ClassifyExpression(string text)
    {
        if (!LicenseExpressionParser.TryParse(text, out var expression))
        {
            // Stryker disable once block : Classify(null) also returns Unknown via the discard arm
            return LicenseClassification.Unknown;
        }

        return Classify(expression!);
    }

    private static LicenseClassification Min(LicenseClassification left, LicenseClassification right)
    {
        // Stryker disable once equality : equal classifications return the same value either way
        return left <= right ? left : right;
    }

    private static LicenseClassification Max(LicenseClassification left, LicenseClassification right)
    {
        // Stryker disable once equality : equal classifications return the same value either way
        return left >= right ? left : right;
    }
}
