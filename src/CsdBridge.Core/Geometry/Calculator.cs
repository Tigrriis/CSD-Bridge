using System;
using System.Collections.Generic;
using System.Globalization;

namespace CsdBridge.Core.Geometry
{
    /// <summary>
    /// A small, safe arithmetic evaluator for the calculate tool, so Claude never has to
    /// do sums in its head. No code execution: a hand-written parser over numbers,
    /// named values, + - * / % ^, parentheses and a fixed set of functions.
    /// Angles are in degrees.
    /// </summary>
    public sealed class Calculator
    {
        private static readonly Dictionary<string, double> Constants = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["pi"] = Math.PI,
        };

        private readonly Dictionary<string, double> _values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Make a named value available to later expressions.</summary>
        public void Set(string name, double value)
        {
            if (!IsName(name))
            {
                throw new FormatException($"'{name}' is not a valid name (letters, digits and _; must start with a letter)");
            }

            if (Constants.ContainsKey(name) || Functions.ContainsKey(name))
            {
                throw new FormatException($"'{name}' is a built-in name; choose another");
            }

            _values[name] = value;
        }

        public double Evaluate(string expression)
        {
            var parser = new Parser(expression ?? "", this);
            double result = parser.ParseExpression();
            parser.ExpectEnd();
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                throw new FormatException("the result is not a finite number (division by zero or out-of-range function argument?)");
            }

            return result;
        }

        public static bool IsName(string s)
        {
            if (string.IsNullOrEmpty(s) || !char.IsLetter(s[0]))
            {
                return false;
            }

            foreach (char c in s)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private const double Deg = Math.PI / 180.0;

        private static readonly Dictionary<string, (int Min, int Max, Func<double[], double> Fn)> Functions =
            new Dictionary<string, (int, int, Func<double[], double>)>(StringComparer.OrdinalIgnoreCase)
            {
                ["sqrt"] = (1, 1, a => Math.Sqrt(a[0])),
                ["abs"] = (1, 1, a => Math.Abs(a[0])),
                ["round"] = (1, 2, a => Math.Round(a[0], a.Length > 1 ? (int)a[1] : 0, MidpointRounding.AwayFromZero)),
                ["floor"] = (1, 1, a => Math.Floor(a[0])),
                ["ceil"] = (1, 1, a => Math.Ceiling(a[0])),
                ["min"] = (1, 100, a => { double m = a[0]; foreach (double v in a) { m = Math.Min(m, v); } return m; }),
                ["max"] = (1, 100, a => { double m = a[0]; foreach (double v in a) { m = Math.Max(m, v); } return m; }),
                ["hypot"] = (2, 2, a => Math.Sqrt(a[0] * a[0] + a[1] * a[1])),
                ["sin"] = (1, 1, a => Math.Sin(a[0] * Deg)),
                ["cos"] = (1, 1, a => Math.Cos(a[0] * Deg)),
                ["tan"] = (1, 1, a => Math.Tan(a[0] * Deg)),
                ["asin"] = (1, 1, a => Math.Asin(a[0]) / Deg),
                ["acos"] = (1, 1, a => Math.Acos(a[0]) / Deg),
                ["atan"] = (1, 1, a => Math.Atan(a[0]) / Deg),
                ["atan2"] = (2, 2, a => Math.Atan2(a[0], a[1]) / Deg),
            };

        private sealed class Parser
        {
            private readonly string _s;
            private readonly Calculator _calc;
            private int _i;

            public Parser(string s, Calculator calc)
            {
                _s = s;
                _calc = calc;
            }

            public void ExpectEnd()
            {
                SkipSpace();
                if (_i < _s.Length)
                {
                    throw Error($"unexpected '{_s[_i]}'");
                }
            }

            // expression := term (('+' | '-') term)*
            public double ParseExpression()
            {
                double v = ParseTerm();
                while (true)
                {
                    if (Accept('+'))
                    {
                        v += ParseTerm();
                    }
                    else if (Accept('-'))
                    {
                        v -= ParseTerm();
                    }
                    else
                    {
                        return v;
                    }
                }
            }

            // term := unary (('*' | '/' | '%') unary)*
            private double ParseTerm()
            {
                double v = ParseUnary();
                while (true)
                {
                    if (Accept('*'))
                    {
                        v *= ParseUnary();
                    }
                    else if (Accept('/'))
                    {
                        double d = ParseUnary();
                        if (d == 0)
                        {
                            throw Error("division by zero");
                        }

                        v /= d;
                    }
                    else if (Accept('%'))
                    {
                        double d = ParseUnary();
                        if (d == 0)
                        {
                            throw Error("modulo by zero");
                        }

                        v %= d;
                    }
                    else
                    {
                        return v;
                    }
                }
            }

            // unary := ('-' | '+') unary | power
            private double ParseUnary()
            {
                if (Accept('-'))
                {
                    return -ParseUnary();
                }

                if (Accept('+'))
                {
                    return ParseUnary();
                }

                return ParsePower();
            }

            // power := primary ('^' unary)?   (right-associative)
            private double ParsePower()
            {
                double b = ParsePrimary();
                if (Accept('^'))
                {
                    return Math.Pow(b, ParseUnary());
                }

                return b;
            }

            private double ParsePrimary()
            {
                SkipSpace();
                if (_i >= _s.Length)
                {
                    throw Error("expression ends too early");
                }

                char c = _s[_i];
                if (Accept('('))
                {
                    double v = ParseExpression();
                    Expect(')');
                    return v;
                }

                if (char.IsDigit(c) || c == '.')
                {
                    return ParseNumber();
                }

                if (char.IsLetter(c))
                {
                    string name = ParseName();
                    SkipSpace();
                    if (_i < _s.Length && _s[_i] == '(')
                    {
                        return CallFunction(name);
                    }

                    if (_calc._values.TryGetValue(name, out double value) || Constants.TryGetValue(name, out value))
                    {
                        return value;
                    }

                    throw Error($"unknown name '{name}'");
                }

                throw Error($"unexpected '{c}'");
            }

            private double CallFunction(string name)
            {
                if (!Functions.TryGetValue(name, out var fn))
                {
                    throw Error($"unknown function '{name}' (available: {string.Join(", ", Functions.Keys)})");
                }

                Expect('(');
                var args = new List<double>();
                SkipSpace();
                if (!Accept(')'))
                {
                    do
                    {
                        args.Add(ParseExpression());
                    }
                    while (Accept(','));
                    Expect(')');
                }

                if (args.Count < fn.Min || args.Count > fn.Max)
                {
                    throw Error(fn.Min == fn.Max
                        ? $"{name}() takes {fn.Min} argument(s)"
                        : $"{name}() takes {fn.Min} to {fn.Max} arguments");
                }

                return fn.Fn(args.ToArray());
            }

            private double ParseNumber()
            {
                int start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'))
                {
                    _i++;
                }

                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    int save = _i;
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
                    {
                        _i++;
                    }

                    if (_i < _s.Length && char.IsDigit(_s[_i]))
                    {
                        while (_i < _s.Length && char.IsDigit(_s[_i]))
                        {
                            _i++;
                        }
                    }
                    else
                    {
                        _i = save; // 'e' starts a name, not an exponent
                    }
                }

                string text = _s.Substring(start, _i - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    throw Error($"'{text}' is not a number");
                }

                return v;
            }

            private string ParseName()
            {
                int start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
                {
                    _i++;
                }

                return _s.Substring(start, _i - start);
            }

            private bool Accept(char c)
            {
                SkipSpace();
                if (_i < _s.Length && _s[_i] == c)
                {
                    _i++;
                    return true;
                }

                return false;
            }

            private void Expect(char c)
            {
                if (!Accept(c))
                {
                    throw Error($"expected '{c}'");
                }
            }

            private void SkipSpace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                {
                    _i++;
                }
            }

            private FormatException Error(string message) =>
                new FormatException($"{message} at position {_i + 1}");
        }
    }
}
