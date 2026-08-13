using CPMigrate.Licensing;
using FluentAssertions;

namespace CPMigrate.Tests.Licensing;

/// <summary>
/// Classifies SPDX identifiers, not package names. A package-name table is how LicenseRisk
/// silently passed every package it had never heard of.
/// </summary>
public class LicenseRiskClassifierTests
{
    [Theory]
    [InlineData("MIT")]
    [InlineData("mit")]
    [InlineData("MIT-0")]
    [InlineData("Apache-2.0")]
    [InlineData("Apache-1.0")]
    [InlineData("Apache-1.1")]
    [InlineData("BSD-2-Clause")]
    [InlineData("BSD-3-Clause")]
    [InlineData("BSD-3-Clause-Clear")]
    [InlineData("ISC")]
    [InlineData("0BSD")]
    [InlineData("Unlicense")]
    [InlineData("CC0-1.0")]
    [InlineData("BlueOak-1.0.0")]
    [InlineData("PostgreSQL")]
    [InlineData("Python-2.0")]
    [InlineData("WTFPL")]
    [InlineData("Zlib")]
    [InlineData("BSL-1.0")]
    [InlineData("MS-PL")]
    [InlineData("NCSA")]
    public void ClassifyIdentifier_PermissiveSpdxIds_ArePermissive(string id)
    {
        LicenseRiskClassifier.ClassifyIdentifier(id).Should().Be(LicenseClassification.Permissive);
    }

    [Theory]
    [InlineData("LGPL-2.0")]
    [InlineData("LGPL-2.0-only")]
    [InlineData("LGPL-2.0-or-later")]
    [InlineData("LGPL-2.1")]
    [InlineData("LGPL-2.1-only")]
    [InlineData("LGPL-2.1-or-later")]
    [InlineData("LGPL-3.0")]
    [InlineData("LGPL-3.0-only")]
    [InlineData("LGPL-3.0-or-later")]
    [InlineData("MPL-1.0")]
    [InlineData("MPL-1.1")]
    [InlineData("MPL-2.0")]
    [InlineData("EPL-1.0")]
    [InlineData("EPL-2.0")]
    [InlineData("CDDL-1.0")]
    [InlineData("CDDL-1.1")]
    [InlineData("CPL-1.0")]
    [InlineData("MS-RL")]
    public void ClassifyIdentifier_WeakCopyleftSpdxIds_AreWeakCopyleft(string id)
    {
        LicenseRiskClassifier.ClassifyIdentifier(id).Should().Be(LicenseClassification.WeakCopyleft);
    }

    [Theory]
    [InlineData("GPL-1.0")]
    [InlineData("GPL-1.0-only")]
    [InlineData("GPL-1.0-or-later")]
    [InlineData("GPL-1.0+")]
    [InlineData("GPL-2.0")]
    [InlineData("GPL-2.0-only")]
    [InlineData("GPL-2.0-or-later")]
    [InlineData("GPL-2.0+")]
    [InlineData("GPL-3.0")]
    [InlineData("GPL-3.0-only")]
    [InlineData("GPL-3.0-or-later")]
    [InlineData("GPL-3.0+")]
    [InlineData("AGPL-1.0")]
    [InlineData("AGPL-1.0-only")]
    [InlineData("AGPL-1.0-or-later")]
    [InlineData("AGPL-3.0")]
    [InlineData("AGPL-3.0-only")]
    [InlineData("AGPL-3.0-or-later")]
    [InlineData("EUPL-1.1")]
    [InlineData("EUPL-1.2")]
    [InlineData("SSPL-1.0")]
    public void ClassifyIdentifier_StrongCopyleftSpdxIds_AreStrongCopyleft(string id)
    {
        LicenseRiskClassifier.ClassifyIdentifier(id).Should().Be(LicenseClassification.StrongCopyleft);
    }

    [Theory]
    [InlineData("BUSL-1.1")]
    [InlineData("Elastic-2.0")]
    [InlineData("CC-BY-NC-4.0")]
    [InlineData("CC-BY-NC-SA-4.0")]
    [InlineData("CC-BY-NC-ND-4.0")]
    [InlineData("CC-BY-NC-3.0")]
    [InlineData("Proprietary")]
    [InlineData("Commercial")]
    public void ClassifyIdentifier_ProprietaryIds_AreProprietary(string id)
    {
        LicenseRiskClassifier.ClassifyIdentifier(id).Should().Be(LicenseClassification.Proprietary);
    }

    [Theory]
    [InlineData("SomeCustomLicense")]
    [InlineData("LicenseRef-Acme")]
    [InlineData("")]
    public void ClassifyIdentifier_UnknownIds_AreUnknown(string id)
    {
        LicenseRiskClassifier.ClassifyIdentifier(id).Should().Be(LicenseClassification.Unknown);
    }

    [Fact]
    public void Classify_OrPrefersTheMostPermissiveSide()
    {
        var expression = new LicenseOr(new LicenseIdentifier("GPL-2.0-only"), new LicenseIdentifier("MIT"));

        LicenseRiskClassifier.Classify(expression).Should().Be(LicenseClassification.Permissive);
    }

    [Fact]
    public void Classify_AndPrefersTheMostRestrictiveSide()
    {
        var expression = new LicenseAnd(new LicenseIdentifier("MIT"), new LicenseIdentifier("GPL-2.0-only"));

        LicenseRiskClassifier.Classify(expression).Should().Be(LicenseClassification.StrongCopyleft);
    }

    [Fact]
    public void Classify_UnknownAndPermissive_IsUnknown()
    {
        var expression = new LicenseAnd(new LicenseIdentifier("MIT"), new LicenseIdentifier("LicenseRef-Acme"));

        LicenseRiskClassifier.Classify(expression).Should().Be(LicenseClassification.Unknown);
    }

    [Fact]
    public void Classify_UnknownOrPermissive_IsPermissive()
    {
        var expression = new LicenseOr(new LicenseIdentifier("MIT"), new LicenseIdentifier("LicenseRef-Acme"));

        LicenseRiskClassifier.Classify(expression).Should().Be(LicenseClassification.Permissive);
    }

    [Fact]
    public void Classify_WithKeepsTheUnderlyingLicenseClassification()
    {
        var expression = new LicenseWith(new LicenseIdentifier("GPL-2.0-only"), "Classpath-exception-2.0");

        LicenseRiskClassifier.Classify(expression).Should().Be(LicenseClassification.StrongCopyleft);
    }

    [Fact]
    public void Classify_WithDoesNotUpgradeAPermissiveLicense()
    {
        var expression = new LicenseWith(new LicenseIdentifier("MIT"), "LLVM-exception");

        LicenseRiskClassifier.Classify(expression).Should().Be(LicenseClassification.Permissive);
    }

    [Fact]
    public void ClassifyExpression_UnparseableText_IsUnknown()
    {
        LicenseRiskClassifier.ClassifyExpression("MIT AND").Should().Be(LicenseClassification.Unknown);
    }
}
