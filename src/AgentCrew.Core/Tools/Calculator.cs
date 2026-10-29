using System.Globalization;

namespace AgentCrew.Core.Tools;

/// <summary>
/// Безопасный калькулятор (рекурсивный спуск) — LLM плохо считает в уме, поэтому
/// все вычисления аналитик делает через этот инструмент. Никакого eval/скриптов.
/// Поддерживает + - * / ^, скобки, унарный минус, функции round, sqrt, abs, min, max, pct.
/// </summary>
public sealed class Calculator
{
    private readonly string _text;
    private int _pos;

    private Calculator(string text) => _text = text;

    public static double Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new FormatException("Пустое выражение.");
        }

        var parser = new Calculator(expression.Replace('×', '*').Replace('÷', '/'));
        var value = parser.ParseExpression();
        parser.SkipSpaces();
        if (parser._pos < parser._text.Length)
        {
            throw new FormatException($"Неожиданный символ '{parser._text[parser._pos]}' в позиции {parser._pos + 1}.");
        }
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArithmeticException("Результат не определён (деление на ноль?).");
        }
        return value;
    }

    // expression := term (('+' | '-') term)*
    private double ParseExpression()
    {
        var value = ParseTerm();
        while (true)
        {
            if (Match('+')) value += ParseTerm();
            else if (Match('-')) value -= ParseTerm();
            else return value;
        }
    }

    // term := power (('*' | '/') power)*
    private double ParseTerm()
    {
        var value = ParsePower();
        while (true)
        {
            if (Match('*')) value *= ParsePower();
            else if (Match('/'))
            {
                var divisor = ParsePower();
                if (divisor == 0)
                {
                    throw new DivideByZeroException("Деление на ноль.");
                }
                value /= divisor;
            }
            else return value;
        }
    }

    // power := unary ('^' power)?   (правоассоциативно)
    private double ParsePower()
    {
        var value = ParseUnary();
        return Match('^') ? Math.Pow(value, ParsePower()) : value;
    }

    private double ParseUnary()
    {
        if (Match('-')) return -ParseUnary();
        if (Match('+')) return ParseUnary();
        return ParsePrimary();
    }

    private double ParsePrimary()
    {
        SkipSpaces();
        if (Match('('))
        {
            var value = ParseExpression();
            Expect(')');
            return value;
        }

        if (_pos < _text.Length && char.IsLetter(_text[_pos]))
        {
            return ParseFunction();
        }

        var start = _pos;
        while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == '_'))
        {
            _pos++;
        }
        if (start == _pos)
        {
            throw new FormatException($"Ожидалось число в позиции {_pos + 1}.");
        }
        var token = _text[start.._pos].Replace("_", "");
        return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private double ParseFunction()
    {
        var start = _pos;
        while (_pos < _text.Length && char.IsLetter(_text[_pos]))
        {
            _pos++;
        }
        var name = _text[start.._pos].ToLowerInvariant();
        Expect('(');
        var args = new List<double> { ParseExpression() };
        while (Match(','))
        {
            args.Add(ParseExpression());
        }
        Expect(')');

        return (name, args.Count) switch
        {
            ("sqrt", 1) => Math.Sqrt(args[0]),
            ("abs", 1) => Math.Abs(args[0]),
            ("round", 1) => Math.Round(args[0], MidpointRounding.AwayFromZero),
            ("round", 2) => Math.Round(args[0], (int)args[1], MidpointRounding.AwayFromZero),
            ("min", >= 1) => args.Min(),
            ("max", >= 1) => args.Max(),
            // pct(a, b) — сколько процентов a составляет от b.
            ("pct", 2) when args[1] != 0 => 100 * args[0] / args[1],
            _ => throw new FormatException($"Неизвестная функция или неверное число аргументов: {name}({args.Count})."),
        };
    }

    private bool Match(char c)
    {
        SkipSpaces();
        if (_pos < _text.Length && _text[_pos] == c)
        {
            _pos++;
            return true;
        }
        return false;
    }

    private void Expect(char c)
    {
        if (!Match(c))
        {
            throw new FormatException($"Ожидался символ '{c}' в позиции {_pos + 1}.");
        }
    }

    private void SkipSpaces()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }
    }
}
