using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClearCalc
{
    public enum AngleMode { DEG, RAD, GRAD }

    public sealed class CalculationException : Exception
    {
        public int Position { get; private set; }
        public CalculationException(string message, int position) : base(message) { Position = position; }
    }

    // A bounded recursive-descent parser. User input is never executed as code.
    public sealed class ExpressionEngine
    {
        private string input;
        private int cursor, depth, steps;
        private AngleMode angle;
        private double answer, memory;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public double Evaluate(string expression, AngleMode mode, double lastAnswer, double memoryValue)
        {
            if (String.IsNullOrWhiteSpace(expression)) throw new CalculationException("请输入表达式", 0);
            if (expression.Length > 16384) throw new CalculationException("表达式过长（最多 16,384 个字符）", 0);
            input = expression.Replace('×', '*').Replace('÷', '/').Replace('−', '-')
                .Replace('（', '(').Replace('）', ')').Replace('，', ',').Replace('＋', '+');
            cursor = depth = steps = 0;
            angle = mode; answer = lastAnswer; memory = memoryValue;
            double value = Sum();
            Skip();
            if (cursor != input.Length) throw Error("无法识别“" + input[cursor] + "”");
            return Finite(value);
        }

        public double Evaluate(string expression) { return Evaluate(expression, AngleMode.DEG, 0, 0); }

        public static string Format(double value, bool scientific)
        {
            if (value == 0) value = 0; // Suppress negative zero.
            return value.ToString(scientific ? "0.##############E+0" : "G15", Invariant);
        }

        public static string Literal(double value) { return value.ToString("R", Invariant); }

        private CalculationException Error(string message) { return new CalculationException(message, cursor); }
        private void Skip() { while (cursor < input.Length && Char.IsWhiteSpace(input[cursor])) cursor++; }
        private bool Take(char ch)
        {
            Skip();
            if (cursor < input.Length && input[cursor] == ch) { cursor++; return true; }
            return false;
        }
        private bool Word(string word)
        {
            Skip();
            if (cursor + word.Length > input.Length || String.Compare(input, cursor, word, 0, word.Length, true, Invariant) != 0) return false;
            int end = cursor + word.Length;
            if (end < input.Length && (Char.IsLetter(input[end]) || input[end] == '_')) return false;
            cursor = end;
            return true;
        }
        private void Enter()
        {
            if (++depth > 128 || ++steps > 50000) throw Error("表达式嵌套过深或过于复杂");
        }
        private double Finite(double value)
        {
            if (Double.IsNaN(value)) throw Error("此运算在实数范围内没有结果");
            if (Double.IsInfinity(value)) throw Error("结果超出可表示范围");
            return value;
        }
        private double Sum()
        {
            Enter();
            try
            {
                double value = Product();
                while (true)
                {
                    if (Take('+')) value = Finite(value + Product());
                    else if (Take('-')) value = Finite(value - Product());
                    else return value;
                }
            }
            finally { depth--; }
        }
        private double Product()
        {
            double value = Unary();
            while (true)
            {
                if (Take('*')) value = Finite(value * Unary());
                else if (Take('/'))
                {
                    double divisor = Unary();
                    if (divisor == 0) throw Error("不能除以零");
                    value = Finite(value / divisor);
                }
                else if (Word("mod"))
                {
                    double divisor = Unary();
                    if (divisor == 0) throw Error("取模的除数不能为零");
                    value = Finite(value % divisor);
                }
                else if (StartsImplicitFactor()) value = Finite(value * Unary());
                else return value;
            }
        }
        private bool StartsImplicitFactor()
        {
            Skip();
            if (cursor >= input.Length) return false;
            char ch = input[cursor];
            return ch == '(' || ch == 'π' || ch == '√' || Char.IsLetter(ch);
        }
        private double Unary()
        {
            Enter();
            try
            {
                if (Take('+')) return Unary();
                if (Take('-')) return -Unary();
                return Power();
            }
            finally { depth--; }
        }
        private double Power()
        {
            double value = Postfix();
            if (Take('^')) value = Pow(value, Unary()); // right associative; -2^2 = -4
            return value;
        }
        private double Postfix()
        {
            double value = Primary();
            while (true)
            {
                if (Take('!')) value = Factorial(value);
                else if (Take('%')) value /= 100;
                else return value;
            }
        }
        private double Primary()
        {
            Skip();
            if (Take('('))
            {
                double value = Sum();
                if (!Take(')')) throw Error("缺少右括号 )");
                return value;
            }
            if (cursor >= input.Length) throw Error("表达式尚未完成");
            char ch = input[cursor];
            if (Char.IsDigit(ch) || ch == '.') return Number();
            if (ch == 'π') { cursor++; return Math.PI; }
            if (ch == '√') { cursor++; return Finite(Math.Sqrt(Unary())); }
            if (Char.IsLetter(ch))
            {
                int start = cursor;
                while (cursor < input.Length && (Char.IsLetter(input[cursor]) || input[cursor] == '_')) cursor++;
                string name = input.Substring(start, cursor - start).ToLowerInvariant();
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                    case "ans": return answer;
                    case "mem": return memory;
                }
                if (!Take('(')) throw Error("函数 " + name + " 后需要括号");
                var args = new List<double>();
                if (!Take(')'))
                {
                    do
                    {
                        if (args.Count >= 64) throw Error("函数参数过多");
                        args.Add(Sum());
                    } while (Take(','));
                    if (!Take(')')) throw Error("缺少右括号 ) 或参数分隔符 ,");
                }
                return Finite(Function(name, args));
            }
            throw Error("这里需要一个数字、常量或函数");
        }
        private double Number()
        {
            int start = cursor;
            while (cursor < input.Length && Char.IsDigit(input[cursor])) cursor++;
            if (cursor < input.Length && input[cursor] == '.')
            {
                cursor++;
                while (cursor < input.Length && Char.IsDigit(input[cursor])) cursor++;
            }
            if (cursor < input.Length && (input[cursor] == 'e' || input[cursor] == 'E'))
            {
                int next = cursor + 1;
                if (next < input.Length && (input[next] == '+' || input[next] == '-')) next++;
                if (next < input.Length && Char.IsDigit(input[next]))
                {
                    cursor = next + 1;
                    while (cursor < input.Length && Char.IsDigit(input[cursor])) cursor++;
                }
                else if (input[cursor] == 'E') throw Error("科学计数法 E 后需要整数指数");
            }
            double value;
            if (!Double.TryParse(input.Substring(start, cursor - start), NumberStyles.Float, Invariant, out value))
                throw Error("数字格式无效或超出范围");
            return Finite(value);
        }
        private double Pow(double x, double y)
        {
            if (x == 0 && y < 0) throw Error("零不能取负数次幂");
            return Finite(Math.Pow(x, y));
        }
        private double Factorial(double x)
        {
            if (x < 0 || x != Math.Truncate(x)) throw Error("阶乘仅支持非负整数");
            if (x > 170) throw Error("阶乘最大支持 170!");
            double value = 1;
            for (int i = 2; i <= (int)x; i++) value *= i;
            return value;
        }
        private double ToRadians(double x)
        {
            if (angle == AngleMode.RAD) return Math.IEEERemainder(x, 2 * Math.PI);
            double period = angle == AngleMode.DEG ? 360 : 400;
            return Math.IEEERemainder(x, period) / period * (2 * Math.PI);
        }
        private double FromRadians(double x)
        {
            if (angle == AngleMode.DEG) return x / Math.PI * 180;
            if (angle == AngleMode.GRAD) return x / Math.PI * 200;
            return x;
        }
        private double Sin(double x)
        {
            double radians = ToRadians(x);
            if (radians == 0 || Math.Abs(radians) == Math.PI) return 0;
            return Math.Sin(radians);
        }
        private double Cos(double x)
        {
            double radians = ToRadians(x);
            if (Math.Abs(radians) == Math.PI / 2) return 0;
            return Math.Cos(radians);
        }
        private double Divide(double x, double y)
        {
            if (y == 0) throw Error("函数在此处无定义");
            return x / y;
        }
        private void Arity(string name, List<double> args, int min, int max)
        {
            if (args.Count < min || args.Count > max)
                throw Error(name + " 需要 " + (min == max ? min.ToString() : min + "～" + max) + " 个参数");
        }
        private double Function(string name, List<double> args)
        {
            int max = name == "log" || name == "round" ? 2 : 1;
            if (name == "pow" || name == "root" || name == "mod") { Arity(name, args, 2, 2); }
            else if (name == "min" || name == "max") { Arity(name, args, 1, 64); }
            else { Arity(name, args, 1, max); }
            double x = args[0], y = args.Count > 1 ? args[1] : 0;
            switch (name)
            {
                case "sin": return Sin(x);
                case "cos": return Cos(x);
                case "tan": return Divide(Sin(x), Cos(x));
                case "sec": return Divide(1, Cos(x));
                case "csc": return Divide(1, Sin(x));
                case "cot": return Divide(Cos(x), Sin(x));
                case "asin": return FromRadians(Math.Asin(x));
                case "acos": return FromRadians(Math.Acos(x));
                case "atan": return FromRadians(Math.Atan(x));
                case "asec": return FromRadians(Math.Acos(Divide(1, x)));
                case "acsc": return FromRadians(Math.Asin(Divide(1, x)));
                case "acot": return FromRadians(Math.Atan2(1, x));
                case "sinh": return Math.Sinh(x);
                case "cosh": return Math.Cosh(x);
                case "tanh": return Math.Tanh(x);
                case "sech": return Divide(1, Math.Cosh(x));
                case "csch": return Divide(1, Math.Sinh(x));
                case "coth": return Divide(1, Math.Tanh(x));
                case "asinh": return Asinh(x);
                case "acosh": return Acosh(x);
                case "atanh": return Atanh(x);
                case "asech": return Acosh(Divide(1, x));
                case "acsch": return Asinh(Divide(1, x));
                case "acoth": return Atanh(Divide(1, x));
                case "sqrt": return Math.Sqrt(x);
                case "cbrt": return Math.Sign(x) * Math.Pow(Math.Abs(x), 1.0 / 3);
                case "root":
                    if (y == 0) throw Error("根指数不能为零");
                    if (x < 0 && y == Math.Truncate(y) && Math.Abs(y % 2) == 1)
                        return -Pow(-x, 1 / y);
                    return Pow(x, 1 / y);
                case "pow": return Pow(x, y);
                case "abs": return Math.Abs(x);
                case "inv": return Divide(1, x);
                case "exp": return Math.Exp(x);
                case "ln":
                    if (x <= 0) throw Error("对数的真数必须大于零");
                    return Math.Log(x);
                case "log":
                    if (x <= 0) throw Error("对数的真数必须大于零");
                    if (args.Count == 1) return Math.Log10(x);
                    if (y <= 0 || y == 1) throw Error("对数的底必须大于零且不等于 1");
                    return Math.Log(x) / Math.Log(y);
                case "floor": return Math.Floor(x);
                case "ceil": return Math.Ceiling(x);
                case "trunc": return Math.Truncate(x);
                case "round":
                    if (args.Count == 1) return Math.Round(x, MidpointRounding.AwayFromZero);
                    if (y != Math.Truncate(y) || y < 0 || y > 15) throw Error("舍入位数应为 0～15 的整数");
                    return Math.Round(x, (int)y, MidpointRounding.AwayFromZero);
                case "fact": return Factorial(x);
                case "sign": return Math.Sign(x);
                case "mod": if (y == 0) throw Error("取模的除数不能为零"); return x % y;
                case "min": { double value = x; foreach (double item in args) value = Math.Min(value, item); return value; }
                case "max": { double value = x; foreach (double item in args) value = Math.Max(value, item); return value; }
                case "dms":
                    double degrees = Math.Floor(Math.Abs(x));
                    double minutes = (Math.Abs(x) - degrees) * 60;
                    double wholeMinutes = Math.Floor(minutes);
                    return Math.Sign(x) * (degrees + wholeMinutes / 100 + (minutes - wholeMinutes) * 60 / 10000);
                case "deg":
                    double whole = Math.Floor(Math.Abs(x));
                    double minutePart = Math.Round((Math.Abs(x) - whole) * 100, 10);
                    double minute = Math.Floor(minutePart);
                    double second = Math.Round((minutePart - minute) * 100, 8);
                    if (minute >= 60 || second >= 60) throw Error("度分秒格式应为 D.MMSS，分、秒必须小于 60");
                    return Math.Sign(x) * (whole + minute / 60 + second / 3600);
                default: throw Error("未知函数：" + name);
            }
        }
        private double Asinh(double x)
        {
            double a = Math.Abs(x);
            if (a < 1e-8) return x;
            return Math.Sign(x) * (a > 1e150 ? Math.Log(a) + Math.Log(2) : Math.Log(a + Math.Sqrt(a * a + 1)));
        }
        private double Acosh(double x)
        {
            if (x < 1) throw Error("acosh 的参数必须大于或等于 1");
            return x > 1e150 ? Math.Log(x) + Math.Log(2) : Math.Log(x + Math.Sqrt(x - 1) * Math.Sqrt(x + 1));
        }
        private double Atanh(double x)
        {
            if (Math.Abs(x) >= 1) throw Error("atanh 的参数必须在 -1 与 1 之间");
            if (Math.Abs(x) < 1e-8) return x;
            return 0.5 * Math.Log((1 + x) / (1 - x));
        }
    }
}
