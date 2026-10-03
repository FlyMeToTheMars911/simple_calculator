using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace ClearCalc
{
    public sealed class HistoryEntry
    {
        public string Expression { get; set; }
        public string Result { get; set; }
        public double? Value { get; set; }
        public AngleMode Mode { get; set; }
        public double AnswerContext { get; set; }
        public double MemoryContext { get; set; }
        public DateTime Time { get; set; }
        public int Number { get; set; }
        public bool IsFinancial { get; set; }
        public bool IsCurrencyConversion { get; set; }
        public string ExactValue { get; set; }
        public bool IsError { get { return !Value.HasValue; } }
        public string Caption { get { return "#" + Number.ToString("D2") + "   ·   " + Time.ToString("HH:mm:ss") + "   ·   " + (IsCurrencyConversion ? "汇率转换" : IsFinancial ? "投资计算" : Mode.ToString()); } }
        public string ResultLine { get { return IsError ? Result : "= " + Result; } }
        public string ResultColor { get { return IsError ? "#B42318" : "#1266BC"; } }
        public string ContextHint
        {
            get { return IsFinancial ? "使用十进制金额计算；回填将带入结果。" : "计算时 Ans = " + ExpressionEngine.Literal(AnswerContext) + "；mem = " + ExpressionEngine.Literal(MemoryContext); }
        }
        public string CopyText { get { return Expression + Environment.NewLine + ResultLine + "  [" + (IsFinancial ? "十进制金额" : Mode.ToString()) + "]"; } }
    }
    public sealed class MemoryEntry
    {
        public double Value { get; set; }
        public string Display { get { return ExpressionEngine.Format(Value, false); } }
    }

    public sealed class CalculatorSession
    {
        public ObservableCollection<HistoryEntry> History { get; private set; }
        public ObservableCollection<MemoryEntry> Memories { get; private set; }
        public AngleMode Mode { get; set; }
        public double Answer { get; private set; }
        public double Memory { get { return Memories.Count == 0 ? 0 : Memories[0].Value; } }
        public CalculatorSession()
        {
            History = new ObservableCollection<HistoryEntry>();
            Memories = new ObservableCollection<MemoryEntry>();
        }
        public double Preview(string expression)
        {
            return new ExpressionEngine().Evaluate(expression, Mode, Answer, Memory);
        }
        public HistoryEntry Calculate(string expression)
        {
            var entry = new HistoryEntry { Expression = expression, Mode = Mode, AnswerContext = Answer,
                MemoryContext = Memory, Time = DateTime.Now, Number = History.Count + 1 };
            try
            {
                double value = Preview(expression);
                entry.Value = value;
                entry.ExactValue = ExpressionEngine.Literal(value);
                entry.Result = ExpressionEngine.Format(value, false);
                Answer = value;
            }
            catch (CalculationException ex) { entry.Result = ex.Message; }
            History.Insert(0, entry);
            return entry;
        }
        public HistoryEntry RecordFinancial(string description, decimal result, bool currencyConversion = false)
        {
            var entry = new HistoryEntry { Expression = description, Value = (double)result, ExactValue = Money.Exact(result),
                Result = Money.Exact(result), IsFinancial = true, IsCurrencyConversion = currencyConversion,
                Time = DateTime.Now, Number = History.Count + 1, Mode = Mode };
            History.Insert(0, entry);
            return entry;
        }
        public void Store(double value) { Memories.Insert(0, new MemoryEntry { Value = value }); }
        public void AddMemory(double delta)
        {
            double value = Memory + delta;
            if (Double.IsInfinity(value) || Double.IsNaN(value)) throw new CalculationException("内存数值超出范围", 0);
            if (Memories.Count == 0) Store(value);
            else Memories[0] = new MemoryEntry { Value = value };
        }
        public string Export()
        {
            var output = new StringBuilder("清算 · 本次会话计算记录" + Environment.NewLine);
            output.AppendLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            output.AppendLine("记录数量：" + History.Count);
            output.AppendLine();
            for (int i = History.Count - 1; i >= 0; i--)
            {
                HistoryEntry entry = History[i];
                output.AppendLine(entry.Caption);
                output.AppendLine(entry.CopyText);
                output.AppendLine(entry.ContextHint);
                output.AppendLine();
            }
            return output.ToString();
        }
    }
}
