using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClearCalc
{
    public static class Money
    {
        public static decimal Parse(string text, string field)
        {
            decimal value;
            if (!Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0)
                throw new FormatException(field + "须为非负数（小数点用 .）");
            return value;
        }
        public static string Display(decimal value) { return value.ToString("N2", CultureInfo.InvariantCulture); }
        public static string Exact(decimal value) { return value.ToString("G29", CultureInfo.InvariantCulture); }
        public static decimal Average(decimal oldQuantity, decimal oldCost, decimal newQuantity, decimal newPrice, decimal fee)
        {
            if (oldQuantity < 0 || oldCost < 0 || newQuantity < 0 || newPrice < 0 || fee < 0) throw new FormatException("数量、价格和费用不能为负数");
            decimal quantity = oldQuantity + newQuantity;
            if (quantity == 0) throw new FormatException("合计数量必须大于零");
            return (oldQuantity * oldCost + newQuantity * newPrice + fee) / quantity;
        }
    }

    public sealed class Holding : INotifyPropertyChanged
    {
        private string name = "新持仓", quantity = "0", buyPrice = "0", currentPrice = "0", buyFee = "0", sellFee = "0";
        public event PropertyChangedEventHandler PropertyChanged;
        public event Action Changed;
        public string Name { get { return name; } set { name = value; Notify("Name"); if (Changed != null) Changed(); } }
        public string QuantityText { get { return quantity; } set { quantity = value; Refresh(); } }
        public string BuyPriceText { get { return buyPrice; } set { buyPrice = value; Refresh(); } }
        public string CurrentPriceText { get { return currentPrice; } set { currentPrice = value; Refresh(); } }
        public string BuyFeeText { get { return buyFee; } set { buyFee = value; Refresh(); } }
        public string SellFeeText { get { return sellFee; } set { sellFee = value; Refresh(); } }
        public decimal Quantity { get; private set; }
        public decimal Cost { get; private set; }
        public decimal Value { get; private set; }
        public decimal Profit { get; private set; }
        public decimal SellFee { get; private set; }
        public decimal? Return { get; private set; }
        public decimal? BreakEven { get; private set; }
        public decimal? Weight { get; private set; }
        public string Error { get; private set; }
        public bool Valid { get { return String.IsNullOrEmpty(Error); } }
        public string CostDisplay { get { return Valid ? Money.Display(Cost) : "—"; } }
        public string ValueDisplay { get { return Valid ? Money.Display(Value) : "—"; } }
        public string ProfitDisplay { get { return Valid ? Money.Display(Profit) : "—"; } }
        public string ReturnDisplay { get { return Valid && Return.HasValue ? Money.Display(Return.Value) + "%" : "—"; } }
        public string BreakEvenDisplay { get { return Valid && BreakEven.HasValue ? BreakEven.Value.ToString("0.######", CultureInfo.InvariantCulture) : "—"; } }
        public string WeightDisplay { get { return Valid && Weight.HasValue ? Money.Display(Weight.Value) + "%" : "—"; } }
        public Holding() { Refresh(); }
        public void Refresh()
        {
            try
            {
                Quantity = Money.Parse(quantity, "持仓数量");
                decimal buy = Money.Parse(buyPrice, "买入均价");
                decimal current = Money.Parse(currentPrice, "当前价格");
                decimal buyCost = Money.Parse(buyFee, "买入费用");
                SellFee = Money.Parse(sellFee, "卖出费用");
                Cost = Quantity * buy + buyCost;
                Value = Quantity * current;
                Profit = Value - SellFee - Cost;
                Return = Cost == 0 ? (decimal?)null : Profit / Cost * 100;
                BreakEven = Quantity == 0 ? (decimal?)null : (Cost + SellFee) / Quantity;
                Error = "";
            }
            catch (FormatException ex) { Error = ex.Message; }
            catch (OverflowException) { Error = "数值过大，无法计算"; }
            Notify("");
            if (Changed != null) Changed();
        }
        public void UpdateWeight(decimal? total)
        {
            Weight = Valid && total.HasValue && total.Value > 0 ? Value / total.Value * 100 : (decimal?)null;
            Notify("WeightDisplay");
        }
        private void Notify(string property)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(property));
        }
    }

    public sealed class Portfolio
    {
        public ObservableCollection<Holding> Holdings { get; private set; }
        public event Action Changed;
        public decimal Cost { get; private set; }
        public decimal Value { get; private set; }
        public decimal Profit { get; private set; }
        public decimal? Return { get; private set; }
        public string Error { get; private set; }
        public bool Valid { get { return String.IsNullOrEmpty(Error); } }
        public Portfolio() { Holdings = new ObservableCollection<Holding>(); Recalculate(); }
        public void Add(Holding item) { Holdings.Add(item); item.Changed += Recalculate; Recalculate(); }
        public void Remove(Holding item) { item.Changed -= Recalculate; Holdings.Remove(item); Recalculate(); }
        public void AddRange(IEnumerable<Holding> items)
        {
            foreach (Holding item in items) { Holdings.Add(item); item.Changed += Recalculate; }
            Recalculate();
        }
        public void Recalculate()
        {
            Cost = Value = Profit = 0; Return = null; Error = "";
            try
            {
                foreach (Holding item in Holdings)
                {
                    if (!item.Valid) { Error = "请修正「" + item.Name + "」：" + item.Error + "；汇总暂停。"; break; }
                    Cost += item.Cost; Value += item.Value; Profit += item.Profit;
                }
                if (Valid && Cost != 0) Return = Profit / Cost * 100;
            }
            catch (OverflowException) { Error = "组合汇总超出可表示范围"; }
            foreach (Holding item in Holdings) item.UpdateWeight(Valid ? Value : (decimal?)null);
            if (Changed != null) Changed();
        }

        private const string Header = "名称,持仓数量,买入均价,当前价格,买入费用,预计卖出费,投入成本,当前市值,净收益,收益率(%),市值占比(%),保本卖价";
        private static string Quote(string text)
        {
            // Neutralize spreadsheet formulas in user-controlled names on CSV export.
            if (!String.IsNullOrEmpty(text) && "=+-@\t\r".IndexOf(text[0]) >= 0) text = "'" + text;
            return "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
        }
        public string ExportCsv()
        {
            if (!Valid) throw new FormatException(Error);
            var output = new StringBuilder(Header + "\r\n");
            foreach (Holding item in Holdings)
            {
                string[] fields = { Quote(item.Name), Money.Exact(Money.Parse(item.QuantityText, "数量")), Money.Exact(Money.Parse(item.BuyPriceText, "均价")),
                    Money.Exact(Money.Parse(item.CurrentPriceText, "现价")), Money.Exact(Money.Parse(item.BuyFeeText, "买入费用")), Money.Exact(item.SellFee),
                    Money.Exact(item.Cost), Money.Exact(item.Value), Money.Exact(item.Profit), item.Return.HasValue ? Money.Exact(item.Return.Value) : "",
                    item.Weight.HasValue ? Money.Exact(item.Weight.Value) : "", item.BreakEven.HasValue ? Money.Exact(item.BreakEven.Value) : "" };
                output.AppendLine(String.Join(",", fields));
            }
            return output.ToString();
        }
        // Parse all rows and validate before changing the current portfolio.
        public static List<Holding> ImportCsv(string csv)
        {
            if (csv.Length > 4 * 1024 * 1024) throw new FormatException("CSV 文件过大（最多 4 MB 文本）");
            List<List<string>> rows = ParseCsv(csv.TrimStart('\uFEFF'));
            if (rows.Count == 0 || rows[0].Count < 6 || rows[0][0] != "名称" || rows[0][1] != "持仓数量" ||
                rows[0][2] != "买入均价" || rows[0][3] != "当前价格" || rows[0][4] != "买入费用" || rows[0][5] != "预计卖出费")
                throw new FormatException("CSV 前六列须为：名称,持仓数量,买入均价,当前价格,买入费用,预计卖出费");
            var items = new List<Holding>();
            for (int i = 1; i < rows.Count; i++)
            {
                List<string> row = rows[i];
                if (row.All(String.IsNullOrWhiteSpace)) continue;
                if (row.Count < 6) throw new FormatException("第 " + (i + 1) + " 行不足六列");
                string name = row[0];
                if (name.Length > 1 && name[0] == '\'' && "=+-@\t\r".IndexOf(name[1]) >= 0) name = name.Substring(1);
                var item = new Holding { Name = name, QuantityText = row[1], BuyPriceText = row[2], CurrentPriceText = row[3], BuyFeeText = row[4], SellFeeText = row[5] };
                if (!item.Valid) throw new FormatException("第 " + (i + 1) + " 行：" + item.Error);
                items.Add(item);
            }
            return items;
        }
        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false, closed = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else { quoted = false; closed = true; }
                    }
                    else field.Append(ch);
                }
                else if (ch == '"' && field.Length == 0 && !closed) quoted = true;
                else if (ch == ',' || ch == '\r' || ch == '\n')
                {
                    row.Add(field.ToString()); field.Clear(); closed = false;
                    if (ch != ',')
                    {
                        rows.Add(row); row = new List<string>();
                        if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    }
                }
                else
                {
                    if (closed || ch == '"') throw new FormatException("CSV 引号格式错误");
                    field.Append(ch);
                }
            }
            if (quoted) throw new FormatException("CSV 引号未闭合");
            if (field.Length > 0 || row.Count > 0 || closed) { row.Add(field.ToString()); rows.Add(row); }
            return rows;
        }
    }
}
