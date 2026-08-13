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
            SkipWhitespace();
        }

        public bool AtEnd => _index >= _text.Length;

        public bool TryParseExpression(out LicenseExpression? expression)
        {
            return TryParseOr(out expression);
        }

        private bool TryParseOr(out LicenseExpression? expression)
        {
            if (!TryParseAnd(out expression) || expression is null)
            {
                return false;
            }

            while (TryReadOperator("OR"))
            {
                if (!TryParseAnd(out var right) || right is null)
                {
                    expression = null;
                    return false;
                }

                expression = new LicenseOr(expression, right);
            }

            return true;
        }

        private bool TryParseAnd(out LicenseExpression? expression)
        {
            if (!TryParseWith(out expression) || expression is null)
            {
                return false;
            }

            while (TryReadOperator("AND"))
            {
                if (!TryParseWith(out var right) || right is null)
                {
                    expression = null;
                    return false;
                }

                expression = new LicenseAnd(expression, right);
            }

            return true;
        }

        private bool TryParseWith(out LicenseExpression? expression)
        {
            if (!TryParsePrimary(out expression) || expression is null)
            {
                return false;
            }

            if (TryReadOperator("WITH"))
            {
                if (!TryReadIdentifier(out var exception) || exception is null)
                {
                    expression = null;
                    return false;
                }

                expression = new LicenseWith(expression, exception);
            }

            return true;
        }

        private bool TryParsePrimary(out LicenseExpression? expression)
        {
            expression = null;
            SkipWhitespace();

            if (TryReadChar('('))
            {
                if (!TryParseExpression(out expression) || expression is null)
                {
                    return false;
                }

                SkipWhitespace();
                if (!TryReadChar(')'))
                {
                    expression = null;
                    return false;
                }

                SkipWhitespace();
                return true;
            }

            if (!TryReadIdentifier(out var id) || id is null)
            {
                return false;
            }

            expression = new LicenseIdentifier(id);
            return true;
        }

        private bool TryReadOperator(string op)
        {
            SkipWhitespace();
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

            // An operator cannot be an identifier used as a license id on its own unless it is
            // actually followed by more expression. TryReadOperator is only called when an operator
            // is expected between terms, so matching the word is enough — but "AND" as a whole
            // expression is rejected because TryParsePrimary would then consume it as an identifier.
            // Operators are reserved words and must not be parsed as identifiers.
            _index = after;
            SkipWhitespace();
            return true;
        }

        private bool TryReadIdentifier(out string? id)
        {
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
