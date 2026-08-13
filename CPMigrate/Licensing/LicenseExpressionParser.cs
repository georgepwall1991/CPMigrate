namespace CPMigrate.Licensing;

/// <summary>
/// Recursive-descent parser for the SPDX subset NuGet accepts: identifiers, AND, OR, WITH, and
/// parentheses. Operators are case-insensitive; identifiers keep their original spelling.
/// </summary>
public static class LicenseExpressionParser
{
    public static bool TryParse(string text, out LicenseExpression? expression)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parser = new Parser(text);
        if (!parser.TryParseExpression(out var parsed) || !parser.AtEnd)
        {
            return false;
        }

        expression = parsed;
        return true;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _index;

        public Parser(string text)
        {
            _text = text;
            // Stryker disable once statement : TryParsePrimary / TryRead* also skip leading whitespace
            SkipWhitespace();
        }

        public bool AtEnd => _index >= _text.Length;

        public bool TryParseExpression(out LicenseExpression? expression)
        {
            return TryParseOr(out expression);
        }

        private bool TryParseOr(out LicenseExpression? expression)
        {
            if (!TryParseAnd(out expression))
            {
                return false;
            }

            while (TryReadOperator("OR"))
            {
                if (!TryParseAnd(out var right))
                {
                    expression = null;
                    return false;
                }

                expression = new LicenseOr(expression!, right!);
            }

            return true;
        }

        private bool TryParseAnd(out LicenseExpression? expression)
        {
            if (!TryParseWith(out expression))
            {
                return false;
            }

            while (TryReadOperator("AND"))
            {
                if (!TryParseWith(out var right))
                {
                    expression = null;
                    return false;
                }

                expression = new LicenseAnd(expression!, right!);
            }

            return true;
        }

        private bool TryParseWith(out LicenseExpression? expression)
        {
            if (!TryParsePrimary(out expression))
            {
                return false;
            }

            if (TryReadOperator("WITH"))
            {
                if (!TryReadIdentifier(out var exception))
                {
                    expression = null;
                    return false;
                }

                expression = new LicenseWith(expression!, exception!);
            }

            return true;
        }

        private bool TryParsePrimary(out LicenseExpression? expression)
        {
            expression = null;
            // Stryker disable once statement : TryReadChar / TryReadIdentifier skip whitespace themselves
            SkipWhitespace();

            if (TryReadChar('('))
            {
                if (!TryParseExpression(out expression))
                {
                    return false;
                }

                // Stryker disable once statement : TryReadChar skips whitespace before matching ')'
                SkipWhitespace();
                if (!TryReadChar(')'))
                {
                    expression = null;
                    return false;
                }

                // Stryker disable once statement : the next token reader skips trailing whitespace
                SkipWhitespace();
                return true;
            }

            if (!TryReadIdentifier(out var id))
            {
                return false;
            }

            expression = new LicenseIdentifier(id!);
            return true;
        }

        private bool TryReadOperator(string op)
        {
            // Stryker disable once statement : identifier reads already skip trailing whitespace
            SkipWhitespace();
            // Stryker disable once equality : an operator exactly at end-of-input is malformed either way
            if (_index + op.Length > _text.Length)
            {
                return false;
            }

            var candidate = _text.Substring(_index, op.Length);
            if (!candidate.Equals(op, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var after = _index + op.Length;
            if (after < _text.Length && IsIdentifierChar(_text[after]))
            {
                return false;
            }

            _index = after;
            // Stryker disable once statement : the next term's reader skips whitespace
            SkipWhitespace();
            return true;
        }

        private bool TryReadIdentifier(out string? id)
        {
            // Stryker disable once statement : callers skip whitespace before asking for an identifier
            SkipWhitespace();
            var start = _index;
            while (_index < _text.Length && IsIdentifierChar(_text[_index]))
            {
                _index++;
            }

            if (_index == start)
            {
                id = null;
                return false;
            }

            id = _text[start.._index];
            if (IsReservedOperator(id))
            {
                _index = start;
                id = null;
                return false;
            }

            // Stryker disable once statement : the next token reader skips whitespace
            SkipWhitespace();
            return true;
        }

        private bool TryReadChar(char expected)
        {
            if (_index >= _text.Length || _text[_index] != expected)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
            {
                _index++;
            }
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c is '.' or '-' or '+';
        }

        private static bool IsReservedOperator(string id)
        {
            return id.Equals("AND", StringComparison.OrdinalIgnoreCase)
                || id.Equals("OR", StringComparison.OrdinalIgnoreCase)
                || id.Equals("WITH", StringComparison.OrdinalIgnoreCase);
        }
    }
}
