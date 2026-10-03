using System;
using System.Collections.Generic;
using System.Globalization;
using ClearCalc;

public static class CoreTests
{
    private static int count;
    private static void Check(bool condition, string message)
    {
        count++;
        if (!condition) throw new Exception("FAIL: " + message);
    }
    private static void Equal(string expression, double expected, AngleMode angle = AngleMode.DEG)
    {
        double actual = new ExpressionEngine().Evaluate(expression, angle, 12, 4);
        Check(Math.Abs(actual - expected) <= 1e-12 * Math.Max(1, Math.Abs(expected)), expression + " => " + actual + "; expected " + expected);
    }
    private static void Error(string expression)
    {
        try { new ExpressionEngine().Evaluate(expression); }
        catch (CalculationException) { Check(true, expression); return; }
        throw new Exception("Expected a calculation error: " + expression);
    }
    private static void InvalidCsv(string csv)
    {
        try { Portfolio.ImportCsv(csv); }
        catch (FormatException) { Check(true, "Invalid CSV"); return; }
        throw new Exception("Expected an import error");
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--rates-smoke")
            {
                ExchangeRateTests.Smoke(Check).GetAwaiter().GetResult(); return 0;
            }
            Equal("2+3*4", 14); Equal("(2+3)*4", 20); Equal("10-3-2", 5);
            Equal("2^3^2", 512); Equal("-2^2", -4); Equal("(-2)^2", 4); Equal("2^-3", 0.125);
            Equal("2*-3", -6); Equal("2--3", 5); Equal("0.1+0.2", 0.3);
            Equal("2π", 2 * Math.PI); Equal("2(3+4)", 14); Equal("(1+2)(3+4)", 21);
            Equal("2sin(30)", 1); Equal("2e", 2 * Math.E); Equal("2.5E6+1e-3", 2500000.001);
            Equal("（2＋3）".Replace('＋','+'), 5); Equal("4×3÷2−1", 5);
            Equal("17 mod 5", 2); Equal("-17 mod 5", -2); Equal("mod(17,5)", 2);
            Equal("200*15%", 30); Equal("100+10%", 100.1); Equal("50%%", 0.005);
            Equal("0!", 1); Equal("5!", 120); Equal("3!^2", 36); Equal("2^3!", 64);
            Equal("sqrt(81)", 9); Equal("√81", 9); Equal("cbrt(-27)", -3);
            Equal("root(-32,5)", -2); Equal("root(81,4)", 3); Equal("root(-8,-3)", -0.5);
            Equal("log(1000)", 3); Equal("log(8,2)", 3); Equal("ln(e)", 1); Equal("exp(2)", Math.Exp(2));
            Equal("sin(30)", 0.5); Equal("cos(60)", 0.5); Equal("tan(45)", 1);
            Equal("sin(180)", 0); Equal("cos(90)", 0); Equal("sec(60)", 2);
            Equal("csc(30)", 2); Equal("cot(45)", 1); Equal("asin(0.5)", 30);
            Equal("acos(0.5)", 60); Equal("atan(1)", 45); Equal("asec(2)", 60);
            Equal("acsc(2)", 30); Equal("acot(-1)", 135);
            Equal("sin(pi/2)", 1, AngleMode.RAD); Equal("asin(1)", Math.PI / 2, AngleMode.RAD);
            Equal("sin(100)", 1, AngleMode.GRAD); Equal("acos(0)", 100, AngleMode.GRAD);
            Equal("sinh(1)", Math.Sinh(1)); Equal("cosh(1)", Math.Cosh(1)); Equal("tanh(1000)", 1);
            Equal("asinh(sinh(-2))", -2); Equal("acosh(cosh(2))", 2); Equal("atanh(tanh(0.5))", 0.5);
            Equal("asinh(-1e200)", -Math.Log(2e200)); Equal("acosh(1e200)", Math.Log(2e200));
            Equal("acsch(csch(1))", 1); Equal("asech(sech(1))", 1); Equal("acoth(coth(1))", 1);
            Equal("abs(-2)+inv(4)", 2.25); Equal("floor(-2.1)", -3); Equal("ceil(-2.1)", -2);
            Equal("trunc(-2.9)", -2); Equal("round(2.5)", 3); Equal("round(2.345,2)", 2.35);
            Equal("min(2,3,-4,5)", -4); Equal("max(2,3,-4,5)", 5); Equal("sign(-3)", -1);
            Equal("dms(12.5)", 12.3); Equal("deg(12.3045)", 12.5125); Equal("deg(dms(-12.5125))", -12.5125);
            Equal("Ans+mem", 16); Equal("(128.6-112.4)*300-8", 4852);
            string[] bad = { "", "(", "1/0", "sqrt(-1)", "ln(0)", "log(-1)", "log(2,1)", "log(2,-3)", "(-2)^0.5", "root(3,0)", "root(-4,2)",
                "171!", "(-2)!", "2.2!", "tan(90)", "sec(270)", "csc(180)", "cot(0)", "asin(2)", "atanh(1)", "acosh(0)", "1E", "1E+",
                "1.2.3", "2 3", "sin()", "sin(1,2)", "log(1,2,3)", "pow(2)", "unknown(1)", "1+", "2)", "1e999", "1e308*10", "mod(2,0)",
                "2 mod 0", "round(1,-1)", "round(1,2.5)", "deg(12.6060)", "0^-1", "1;2", "System.IO.File()" };
            foreach (string expression in bad) Error(expression);
            Error(new string('(', 200) + "1" + new string(')', 200));
            Error(new string('-', 200) + "1"); Error(new string('1', 16385));
            Check(ExpressionEngine.Format(0.1 + 0.2, false) == "0.3", "Readable decimal display");
            Check(ExpressionEngine.Format(125000, true) == "1.25E+5", "Scientific display");
            var session = new CalculatorSession();
            session.Calculate("12"); session.Calculate("Ans*3");
            Check(session.Answer == 36 && session.History.Count == 2, "Session answer and history");
            session.Preview("7+8"); Check(session.History.Count == 2, "Preview not logged");
            session.Calculate("1/0"); Check(session.History[0].IsError && session.Answer == 36, "Failed calculation keeps answer");
            session.Store(10); session.Store(20); session.AddMemory(5);
            Check(session.Memories.Count == 2 && session.Memory == 25 && session.Memories[1].Value == 10, "Multiple memories");
            for (int i = 0; i < 3000; i++) session.Calculate(i.ToString(CultureInfo.InvariantCulture));
            Check(session.History.Count == 3003, "No history truncation");
            string export = session.Export(); Check(export.Contains("Ans*3") && export.Contains("不能除以零"), "Export includes successes and failures");
            Check(Money.Average(100, 10, 200, 7, 6) == 8.02m, "Fee-aware weighted cost");
            Check(Money.Average(0, 0, 3, 0.1m, 0) == 0.1m, "Exact decimal cost");
            var item = new Holding { Name = "A", QuantityText = "300", BuyPriceText = "112.4", CurrentPriceText = "128.6", BuyFeeText = "8", SellFeeText = "12" };
            Check(item.Valid && item.Cost == 33728m && item.Value == 38580m && item.Profit == 4840m, "Holding cost and fee-aware profit");
            Check(item.BreakEven == 33740m / 300m, "Break-even including selling fee");
            var portfolio = new Portfolio(); portfolio.Add(item);
            var cents = new Holding { Name = "分币", QuantityText = "3", BuyPriceText = "0.1", CurrentPriceText = "0.2" }; portfolio.Add(cents);
            Check(cents.Cost == 0.3m && cents.Profit == 0.3m, "No binary floating-point in portfolio");
            Check(portfolio.Cost == 33728.3m && portfolio.Profit == 4840.3m, "Portfolio sums");
            Check(Math.Abs(item.Weight.Value + cents.Weight.Value - 100) < 0.000000000000000000001m, "Portfolio weights sum to 100");
            cents.CurrentPriceText = "oops"; Check(!cents.Valid && !portfolio.Valid && item.Weight == null, "Invalid row suppresses total and weights");
            cents.CurrentPriceText = "0.2"; Check(portfolio.Valid, "Totals recover after correction");
            cents.QuantityText = "-1"; Check(!portfolio.Valid, "Negative positions rejected"); cents.QuantityText = "3";
            item.Name = "包含逗号,引号\"和\n换行";
            string csv = portfolio.ExportCsv(); List<Holding> imported = Portfolio.ImportCsv(csv);
            Check(imported.Count == 2 && imported[0].Name == item.Name && imported[0].Cost == item.Cost, "CSV quote/newline roundtrip");
            cents.Name = "=1+1"; csv = portfolio.ExportCsv(); Check(csv.Contains("\"'=1+1\""), "CSV spreadsheet name escaping");
            Check(Portfolio.ImportCsv(csv)[1].Name == "=1+1", "CSV escaped name reimport");
            InvalidCsv("not,a,portfolio"); InvalidCsv("名称,持仓数量,买入均价,当前价格,买入费用,预计卖出费\nA,bad,1,1,0,0");
            InvalidCsv("名称,持仓数量,买入均价,当前价格,买入费用,预计卖出费\n\"unfinished");
            InvalidCsv("名称,持仓数量,买入均价,当前价格,买入费用,预计卖出费\n\"A\"junk,1,1,1,0,0");
            var huge = new Holding { QuantityText = "79228162514264337593543950335", BuyPriceText = "2" };
            Check(!huge.Valid, "Decimal overflow reported");
            var empty = new Holding(); Check(empty.Valid && empty.Return == null && empty.BreakEven == null, "Zero cost and quantity avoid division");
            session.RecordFinancial("精确金额", 12345678901234567890.12345678m);
            Check(session.History[0].ExactValue == "12345678901234567890.12345678", "Financial history preserves decimal precision");
            ExchangeRateTests.Run(Check).GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + count + " checks (parser, scientific functions, errors, session history, decimal finance, CSV, exchange rates). ");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
