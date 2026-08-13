using CPMigrate.Licensing;
using FluentAssertions;

namespace CPMigrate.Tests.Licensing;

/// <summary>
/// NuGet license expressions are SPDX with AND, OR, WITH, and parentheses. A regex cannot tell
/// those operators apart, and swapping AND for OR is exactly the mutant that would hide GPL
/// behind MIT.
/// </summary>
public class LicenseExpressionParserTests
{
    [Fact]
    public void TryParse_SingleIdentifier_ReturnsIdentifier()
    {
        LicenseExpressionParser.TryParse("MIT", out var expression).Should().BeTrue();
        expression.Should().Be(new LicenseIdentifier("MIT"));
    }

    [Fact]
    public void TryParse_PreservesIdentifierCasing()
    {
        LicenseExpressionParser.TryParse("Apache-2.0", out var expression).Should().BeTrue();
        expression.Should().Be(new LicenseIdentifier("Apache-2.0"));
    }

    [Fact]
    public void TryParse_AndIsLeftAssociative()
    {
        LicenseExpressionParser.TryParse("MIT AND Apache-2.0", out var expression).Should().BeTrue();
        expression
            .Should()
            .Be(new LicenseAnd(new LicenseIdentifier("MIT"), new LicenseIdentifier("Apache-2.0")));

        // Kept next to a test that already covers IsReservedOperator so Stryker's per-test
        // coverage actually runs the assertion against the "AND"/"OR"/"WITH" string mutants.
        LicenseExpressionParser.TryParse("AND", out _).Should().BeFalse();
        LicenseExpressionParser.TryParse("OR", out _).Should().BeFalse();
        LicenseExpressionParser.TryParse("WITH", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_OrIsLeftAssociative()
    {
        LicenseExpressionParser.TryParse("GPL-2.0-only OR MIT", out var expression).Should().BeTrue();
        expression
            .Should()
            .Be(new LicenseOr(new LicenseIdentifier("GPL-2.0-only"), new LicenseIdentifier("MIT")));

        // "ORANGE" starts with the OR operator then an identifier character; accepting it as
        // OR + ANGE would silently change a single unknown id into a dual-license expression.
        LicenseExpressionParser.TryParse("MIT ORANGE", out var orange).Should().BeFalse();
        orange.Should().BeNull();
        LicenseExpressionParser.TryParse("MIT ANDY Apache-2.0", out var andy).Should().BeFalse();
        andy.Should().BeNull();
        LicenseExpressionParser.TryParse("GPL-2.0-only WITHHOLDING", out var withholding).Should().BeFalse();
        withholding.Should().BeNull();
    }

    [Fact]
    public void TryParse_AndBindsTighterThanOr()
    {
        // MIT OR Apache-2.0 AND GPL-2.0-only == MIT OR (Apache-2.0 AND GPL-2.0-only)
        LicenseExpressionParser.TryParse("MIT OR Apache-2.0 AND GPL-2.0-only", out var expression).Should().BeTrue();
        expression
            .Should()
            .Be(
                new LicenseOr(
                    new LicenseIdentifier("MIT"),
                    new LicenseAnd(new LicenseIdentifier("Apache-2.0"), new LicenseIdentifier("GPL-2.0-only"))
                )
            );
    }

    [Fact]
    public void TryParse_ParenthesesOverridePrecedence()
    {
        LicenseExpressionParser
            .TryParse("(MIT OR Apache-2.0) AND GPL-2.0-only", out var expression)
            .Should()
            .BeTrue();
        expression
            .Should()
            .Be(
                new LicenseAnd(
                    new LicenseOr(new LicenseIdentifier("MIT"), new LicenseIdentifier("Apache-2.0")),
                    new LicenseIdentifier("GPL-2.0-only")
                )
            );

        LicenseExpressionParser.TryParse("()", out var empty).Should().BeFalse();
        empty.Should().BeNull();
        LicenseExpressionParser.TryParse("(", out var unclosed).Should().BeFalse();
        unclosed.Should().BeNull();
        LicenseExpressionParser.TryParse("(AND)", out var reserved).Should().BeFalse();
        reserved.Should().BeNull();
    }

    [Fact]
    public void TryParse_WithAttachesToTheLicenseNotTheOperator()
    {
        LicenseExpressionParser
            .TryParse("GPL-2.0-only WITH Classpath-exception-2.0", out var expression)
            .Should()
            .BeTrue();
        expression
            .Should()
            .Be(new LicenseWith(new LicenseIdentifier("GPL-2.0-only"), "Classpath-exception-2.0"));
    }

    [Fact]
    public void TryParse_WithBindsTighterThanAnd()
    {
        LicenseExpressionParser
            .TryParse("GPL-2.0-only WITH Classpath-exception-2.0 AND MIT", out var expression)
            .Should()
            .BeTrue();
        expression
            .Should()
            .Be(
                new LicenseAnd(
                    new LicenseWith(new LicenseIdentifier("GPL-2.0-only"), "Classpath-exception-2.0"),
                    new LicenseIdentifier("MIT")
                )
            );
    }

    [Fact]
    public void TryParse_OperatorsAreCaseInsensitive()
    {
        LicenseExpressionParser.TryParse("mit or apache-2.0", out var expression).Should().BeTrue();
        expression
            .Should()
            .Be(new LicenseOr(new LicenseIdentifier("mit"), new LicenseIdentifier("apache-2.0")));
    }

    [Fact]
    public void TryParse_AllowsExtraWhitespace()
    {
        LicenseExpressionParser.TryParse("  MIT   AND   ISC  ", out var expression).Should().BeTrue();
        expression.Should().Be(new LicenseAnd(new LicenseIdentifier("MIT"), new LicenseIdentifier("ISC")));
    }

    [Fact]
    public void TryParse_PlusSuffixIsPartOfTheIdentifier()
    {
        LicenseExpressionParser.TryParse("GPL-2.0+", out var expression).Should().BeTrue();
        expression.Should().Be(new LicenseIdentifier("GPL-2.0+"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MIT AND")]
    [InlineData("AND MIT")]
    [InlineData("MIT OR")]
    [InlineData("(MIT")]
    [InlineData("MIT)")]
    [InlineData("MIT WITH")]
    [InlineData("()")]
    [InlineData("(")]
    public void TryParse_MalformedExpression_ReturnsFalse(string text)
    {
        LicenseExpressionParser.TryParse(text, out var expression).Should().BeFalse();
        expression.Should().BeNull();
    }

    [Fact]
    public void TryParse_DoesNotTreatAndAsAnIdentifier()
    {
        LicenseExpressionParser.TryParse("AND", out var expression).Should().BeFalse();
        expression.Should().BeNull();
    }

    [Theory]
    [InlineData("OR")]
    [InlineData("WITH")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("with")]
    public void TryParse_DoesNotTreatReservedOperatorsAsIdentifiers(string text)
    {
        LicenseExpressionParser.TryParse(text, out var expression).Should().BeFalse();
        expression.Should().BeNull();
    }

    [Fact]
    public void TryParse_TrailingIdentifierWithoutAnOperator_IsMalformed()
    {
        LicenseExpressionParser.TryParse("MIT Apache-2.0", out var expression).Should().BeFalse();
        expression.Should().BeNull();
    }
}
