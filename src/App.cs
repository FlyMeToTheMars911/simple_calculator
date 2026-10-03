using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("清算 · 科学、投资与汇率计算器")]
[assembly: AssemblyDescription("轻量原生桌面计算器：可编辑表达式、会话历史、投资组合与汇率转换")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace ClearCalc
{
    public static class App
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool test = args.Length == 2 && args[0] == "--ui-test";
            try
            {
                var app = new Application();
                var controller = new MainController(test ? new ExchangeRateService(
                    delegate(string from, string to, System.Threading.CancellationToken token)
                    { return System.Threading.Tasks.Task.FromResult(new ExchangeRate(from, to, "2026-10-02", 7.1m)); },
                    delegate { return DateTime.UtcNow; }) : null);
                if (test)
                {
                    controller.Window.WindowStartupLocation = WindowStartupLocation.Manual;
                    controller.Window.Left = -20000; controller.Window.Top = -20000;
                    controller.Window.ShowActivated = false;
                    controller.Window.ContentRendered += async delegate
                    {
                        try { await controller.RunUiChecks(args[1]); app.Shutdown(0); }
                        catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "ui-test-failure.txt"), ex.ToString()); app.Shutdown(1); }
                    };
                }
                return app.Run(controller.Window);
            }
            catch (Exception ex)
            {
                if (test) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "startup-failure.txt"), ex.ToString()); }
                else MessageBox.Show("启动失败：" + ex.Message, "清算", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    public sealed partial class MainController
    {
        public Window Window { get; private set; }
        private readonly CalculatorSession session = new CalculatorSession();
        private readonly Portfolio portfolio = new Portfolio();
        private readonly DispatcherTimer previewTimer = new DispatcherTimer();
        private readonly Random random = new Random();
        private TextBox editor, resultBox;
        private TextBlock resultState, status;
        private ListBox historyList;
        private DataGrid holdingsGrid;
        private double? currentValue;
        private bool scientific, second, historyVisible = true, portfolioVisible;
        private bool committed;
        private string lastPreviewError;
        private const string PortfolioHint = "双击单元格编辑，Tab 切换。所有持仓须使用同一币种，价格由你手动填写。";

        public MainController(ExchangeRateService rates = null)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClearCalc.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            editor = Find<TextBox>("ExpressionBox"); resultBox = Find<TextBox>("ResultBox");
            resultState = Find<TextBlock>("ResultState"); status = Find<TextBlock>("StatusText");
            historyList = Find<ListBox>("HistoryList"); holdingsGrid = Find<DataGrid>("HoldingsGrid");
            historyList.ItemsSource = session.History; holdingsGrid.ItemsSource = portfolio.Holdings;
            previewTimer.Interval = TimeSpan.FromMilliseconds(160);
            previewTimer.Tick += delegate { previewTimer.Stop(); Preview(); UpdateUndo(); };
            editor.TextChanged += delegate
            {
                Find<TextBlock>("ExpressionPlaceholder").Visibility = editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                committed = false; currentValue = null;
                previewTimer.Stop(); previewTimer.Start();
                Find<Button>("UndoButton").IsEnabled = editor.CanUndo;
                Find<Button>("RedoButton").IsEnabled = editor.CanRedo;
            };
            Window.PreviewKeyDown += KeyDown;
            Window.SizeChanged += delegate { if (!portfolioVisible && !currencyVisible) SetHistory(historyVisible && Window.ActualWidth >= 850); };
            Window.Loaded += delegate { editor.Focus(); UpdateMemory(); Preview(); };
            Find<UniformGrid>("Keypad").AddHandler(Button.ClickEvent, new RoutedEventHandler(KeypadClick));
            historyList.AddHandler(Button.ClickEvent, new RoutedEventHandler(HistoryClick));
            On("CalculatorTab", delegate { ShowPage(false); }); On("PortfolioTab", delegate { ShowPage(true); });
            On("HistoryToggle", delegate
            {
                bool open = Find<Border>("HistoryPanel").Visibility != Visibility.Visible;
                if (portfolioVisible || currencyVisible) { ShowPage(false); open = true; }
                historyVisible = open;
                if (open && Window.Width < 850) Window.Width = 1080;
                SetHistory(open);
            });
            On("UndoButton", delegate { editor.Focus(); editor.Undo(); UpdateUndo(); });
            On("RedoButton", delegate { editor.Focus(); editor.Redo(); UpdateUndo(); });
            On("AngleButton", ShowAngles); On("FormatButton", delegate
            {
                scientific = !scientific;
                Find<Button>("FormatButton").Foreground = Brush(scientific ? "#1769C2" : "#202834");
                if (currentValue.HasValue) resultBox.Text = ExpressionEngine.Format(currentValue.Value, scientific);
            });
            On("AnswerButton", delegate { Insert("Ans"); });
            On("CopyResultButton", CopyResult); On("ContinueButton", ContinueResult);
            On("TrigButton", ShowTrig); On("FunctionButton", ShowFunctions);
            On("MemoryClear", delegate { session.Memories.Clear(); UpdateMemory(); InvalidatePreview(); });
            On("MemoryRecall", delegate { if (session.Memories.Count > 0) InsertLiteral(ExpressionEngine.Literal(session.Memory)); });
            On("MemoryStore", delegate { MemoryOperation("store"); });
            On("MemoryAdd", delegate { MemoryOperation("add"); });
            On("MemorySubtract", delegate { MemoryOperation("subtract"); });
            On("MemoryListButton", ShowMemory);
            On("ExportHistoryButton", ExportHistory);
            On("TopmostButton", delegate { Window.Topmost = !Window.Topmost; Find<Button>("TopmostButton").Content = Window.Topmost ? "已置顶" : "置顶"; });
            On("HelpButton", ShowHelp);
            On("AddHoldingButton", delegate
            {
                var holding = new Holding(); portfolio.Add(holding); holdingsGrid.SelectedItem = holding;
                holdingsGrid.ScrollIntoView(holding); holdingsGrid.CurrentCell = new DataGridCellInfo(holding, holdingsGrid.Columns[0]);
                holdingsGrid.Focus(); holdingsGrid.BeginEdit();
            });
            On("RemoveHoldingButton", delegate
            {
                Holding selected = holdingsGrid.SelectedItem as Holding;
                if (selected != null) { CommitGrid(); portfolio.Remove(selected); }
            });
            On("ImportPortfolioButton", ImportPortfolio); On("ExportPortfolioButton", ExportPortfolio);
            On("RecordPortfolioButton", RecordPortfolio); On("UseHoldingButton", UseHolding);
            On("RecordAverageButton", delegate { Average(true); });
            foreach (string name in AverageFields) Find<TextBox>(name).TextChanged += delegate { Average(false); };
            portfolio.Changed += UpdatePortfolio;
            InitializeCurrencyPage(rates ?? new ExchangeRateService());
            UpdatePortfolio(); UpdateHistory(); UpdateUndo();
        }
        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private static SolidColorBrush Brush(string color) { return (SolidColorBrush)new BrushConverter().ConvertFromString(color); }
        private void On(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }
        private void UpdateUndo() { Find<Button>("UndoButton").IsEnabled = editor.CanUndo; Find<Button>("RedoButton").IsEnabled = editor.CanRedo; }
        private void SetHistory(bool visible)
        {
            Find<Border>("HistoryPanel").Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Find<ColumnDefinition>("HistoryColumn").Width = new GridLength(visible ? 304 : 0);
            Find<ColumnDefinition>("HistoryGap").Width = new GridLength(visible ? 18 : 0);
            Find<Button>("HistoryToggle").Content = visible ? "收起历史  ‹" : "历史记录  ›";
        }
        private void ShowPage(bool investment)
        {
            currencyVisible = false;
            Find<ScrollViewer>("CurrencyPage").Visibility = Visibility.Collapsed;
            Find<Button>("CurrencyTab").Style = (Style)Window.Resources[typeof(Button)];
            portfolioVisible = investment;
            if (investment && Window.Width < 1000) Window.Width = 1080;
            Window.MinWidth = investment ? 1000 : 500;
            Find<Grid>("CalculatorPage").Visibility = investment ? Visibility.Collapsed : Visibility.Visible;
            Find<Grid>("PortfolioPage").Visibility = investment ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("CalculatorTab").Style = (Style)Window.Resources[investment ? typeof(Button) : (object)"Accent"];
            Find<Button>("PortfolioTab").Style = (Style)Window.Resources[investment ? (object)"Accent" : typeof(Button)];
            if (!investment) { SetHistory(historyVisible && Window.ActualWidth >= 850); editor.Focus(); }
        }
        private void Insert(string text, int back = 0)
        {
            editor.Focus();
            editor.BeginChange();
            try
            {
                int start = editor.SelectionStart;
                editor.SelectedText = text;
                editor.Select(start + text.Length - back, 0);
            }
            finally { editor.EndChange(); }
            UpdateUndo();
        }
        private void InsertLiteral(string text) { Insert("(" + text + ")"); }
        private void ReplaceAll(string text)
        {
            editor.Focus(); editor.SelectAll(); Insert(text);
        }
        private void Wrap(string prefix, string suffix, bool wholeExpression)
        {
            if (editor.SelectionLength == 0 && wholeExpression && !String.IsNullOrWhiteSpace(editor.Text)) editor.SelectAll();
            string selected = editor.SelectedText;
            Insert(prefix + selected + suffix, selected.Length == 0 ? suffix.Length : 0);
        }
        private void Backspace()
        {
            if (editor.SelectionLength == 0 && editor.SelectionStart > 0) editor.Select(editor.SelectionStart - 1, 1);
            if (editor.SelectionLength > 0) Insert("");
            editor.Focus();
        }
        private void KeypadClick(object sender, RoutedEventArgs e)
        {
            Button button = e.Source as Button;
            if (button != null && button.Tag != null) Execute(button.Tag.ToString());
        }
        private void Execute(string action)
        {
            switch (action)
            {
                case "equals": Calculate(); break;
                case "clear": ReplaceAll(""); break;
                case "backspace": Backspace(); break;
                case "second":
                    second = !second;
                    Find<Button>("SecondButton").Background = Brush(second ? "#C6DFFA" : "#EAF0F6");
                    Find<Button>("SquareButton").Content = second ? "x³" : "x²";
                    Find<Button>("RootButton").Content = second ? "³√x" : "²√x";
                    Find<Button>("PowerButton").Content = second ? "ʸ√x" : "xʸ";
                    Find<Button>("TenButton").Content = second ? "2ˣ" : "10ˣ";
                    Find<Button>("LogButton").Content = second ? "logᵧx" : "log";
                    Find<Button>("LnButton").Content = second ? "eˣ" : "ln";
                    break;
                case "square": Wrap("(", second ? ")^3" : ")^2", true); break;
                case "root": Wrap(second ? "cbrt(" : "sqrt(", ")", true); break;
                case "reciprocal": Wrap("1/(", ")", true); break;
                case "absolute": Wrap("abs(", ")", true); break;
                case "factorial": Wrap("(", ")!", true); break;
                case "negate": Wrap("-(", ")", true); break;
                case "power": if (second) WrapArgument("root"); else Insert("^"); break;
                case "ten": Wrap(second ? "2^(" : "10^(", ")", true); break;
                case "log": if (second) WrapArgument("log"); else Wrap("log(", ")", true); break;
                case "ln": Wrap(second ? "exp(" : "ln(", ")", true); break;
                default: Insert(action); break;
            }
        }
        private void WrapArgument(string function)
        {
            if (editor.SelectionLength == 0 && !String.IsNullOrWhiteSpace(editor.Text)) editor.SelectAll();
            string value = editor.SelectedText;
            if (value.Length == 0) Insert(function + "(,)", 2);
            else Insert(function + "(" + value + ",)", 1);
        }
        private void KeyDown(object sender, KeyEventArgs e)
        {
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (currencyVisible)
            {
                if (e.Key == Key.Enter && Find<TextBox>("CurrencyAmount").IsKeyboardFocusWithin)
                { RecordConversion(); e.Handled = true; }
                else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
                { CopyConversion(); e.Handled = true; }
                return;
            }
            if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C && !portfolioVisible)
            { CopyResult(); e.Handled = true; return; }
            if (modifiers == ModifierKeys.Control && e.Key == Key.L && !portfolioVisible)
            { editor.Focus(); editor.SelectAll(); e.Handled = true; return; }
            if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z && !portfolioVisible && editor.IsKeyboardFocusWithin)
            { editor.Redo(); UpdateUndo(); e.Handled = true; return; }
            if (portfolioVisible) return;
            if (e.Key == Key.Enter && modifiers != ModifierKeys.Shift) { Calculate(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ReplaceAll(""); e.Handled = true; }
            else if (e.Key == Key.F9) { Execute("negate"); e.Handled = true; }
            else if (e.Key == Key.OemPlus && modifiers == ModifierKeys.None && editor.IsKeyboardFocusWithin)
            { Calculate(); e.Handled = true; }
        }
        private void InvalidatePreview() { committed = false; Preview(); }
        private void Preview()
        {
            if (committed) return;
            currentValue = null; lastPreviewError = null;
            if (String.IsNullOrWhiteSpace(editor.Text))
            { resultBox.Text = "0"; resultState.Text = "支持括号、函数与任意位置编辑"; resultState.Foreground = Brush("#6D7888"); return; }
            try
            {
                currentValue = session.Preview(editor.Text);
                resultBox.Text = ExpressionEngine.Format(currentValue.Value, scientific);
                resultState.Text = "实时预览 · Enter 保存到历史"; resultState.Foreground = Brush("#6D7888");
            }
            catch (CalculationException ex)
            {
                resultBox.Text = "—"; lastPreviewError = ex.Message;
                resultState.Text = ex.Message; resultState.Foreground = Brush("#A36B25");
            }
        }
        private void Calculate()
        {
            previewTimer.Stop();
            if (String.IsNullOrWhiteSpace(editor.Text)) return;
            HistoryEntry entry = session.Calculate(editor.Text);
            committed = true; currentValue = entry.Value; lastPreviewError = entry.IsError ? entry.Result : null;
            resultBox.Text = entry.Value.HasValue ? ExpressionEngine.Format(entry.Value.Value, scientific) : "—";
            resultState.Text = entry.IsError ? entry.Result : "已保存到历史 #" + entry.Number + " · 表达式可继续编辑";
            resultState.Foreground = Brush(entry.IsError ? "#B42318" : "#6D7888");
            UpdateHistory(); editor.Focus();
        }
        private bool Current(out double value)
        {
            if (!committed) Preview();
            value = currentValue.GetValueOrDefault();
            if (currentValue.HasValue) return true;
            status.Text = lastPreviewError ?? "请先输入有效表达式"; return false;
        }
        private void ContinueResult()
        {
            double value; if (Current(out value)) ReplaceAll(ExpressionEngine.Literal(value));
        }
        private void CopyResult()
        {
            double value; if (Current(out value)) Copy(ExpressionEngine.Format(value, scientific));
        }
        private void Copy(string text)
        {
            try { Clipboard.SetText(text); status.Text = "已复制到剪贴板"; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板暂时被占用，请重试"; }
        }
        private void UpdateHistory()
        {
            Find<TextBlock>("HistoryCount").Text = "本次会话 · " + session.History.Count + " 条记录";
            Find<StackPanel>("EmptyHistory").Visibility = session.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("ExportHistoryButton").IsEnabled = session.History.Count > 0;
            if (session.History.Count > 0) historyList.ScrollIntoView(session.History[0]);
        }
        private void HistoryClick(object sender, RoutedEventArgs e)
        {
            Button button = e.Source as Button;
            if (button == null) return;
            HistoryEntry entry = button.DataContext as HistoryEntry;
            if (entry == null) return;
            switch (Convert.ToString(button.Tag))
            {
                case "restore": Restore(entry); break;
                case "reuse": if (entry.Value.HasValue) InsertLiteral(entry.ExactValue); break;
                case "copy": Copy(entry.CopyText); break;
            }
        }
        private void Restore(HistoryEntry entry)
        {
            if (entry.IsFinancial)
            {
                ReplaceAll(entry.ExactValue); status.Text = "已带入十进制计算结果；科学计算使用约 15 位有效数字"; return;
            }
            session.Mode = entry.Mode; UpdateAngle();
            string expression = Regex.Replace(entry.Expression, @"\b(ans|mem)\b", delegate(Match match)
            { return "(" + ExpressionEngine.Literal(match.Value.Equals("ans", StringComparison.OrdinalIgnoreCase) ? entry.AnswerContext : entry.MemoryContext) + ")"; }, RegexOptions.IgnoreCase);
            ReplaceAll(expression); status.Text = "已回填 #" + entry.Number + "，Ans / mem 已固定为原计算时的值";
        }
        private void ShowAngles()
        {
            var menu = new ContextMenu();
            foreach (AngleMode mode in Enum.GetValues(typeof(AngleMode)))
            {
                AngleMode selected = mode;
                AddMenu(menu, mode + (mode == AngleMode.DEG ? " · 度" : mode == AngleMode.RAD ? " · 弧度" : " · 百分度"), delegate { session.Mode = selected; UpdateAngle(); InvalidatePreview(); });
            }
            OpenMenu("AngleButton", menu);
        }
        private void UpdateAngle() { Find<Button>("AngleButton").Content = session.Mode + " ▾"; }
        private void AddMenu(ItemsControl menu, string label, Action action)
        {
            var item = new MenuItem { Header = label }; item.Click += delegate { action(); }; menu.Items.Add(item);
        }
        private void OpenMenu(string name, ContextMenu menu)
        {
            menu.PlacementTarget = Find<Button>(name); menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        }
        private void ShowTrig()
        {
            var menu = new ContextMenu();
            string[][] groups = { new[] { "三角函数", "sin", "cos", "tan", "sec", "csc", "cot" }, new[] { "反三角函数", "asin", "acos", "atan", "asec", "acsc", "acot" },
                new[] { "双曲函数", "sinh", "cosh", "tanh", "sech", "csch", "coth" }, new[] { "反双曲函数", "asinh", "acosh", "atanh", "asech", "acsch", "acoth" } };
            foreach (string[] group in groups)
            {
                var parent = new MenuItem { Header = group[0] };
                foreach (string name in group.Skip(1)) { string f = name; AddMenu(parent, name + "(x)", delegate { Wrap(f + "(", ")", false); }); }
                menu.Items.Add(parent);
            }
            OpenMenu("TrigButton", menu);
        }
        private void ShowFunctions()
        {
            var menu = new ContextMenu();
            string[,] functions = { { "abs", "绝对值 abs(x)" }, { "floor", "向下取整 floor(x)" }, { "ceil", "向上取整 ceil(x)" }, { "trunc", "截取整数 trunc(x)" },
                { "round", "四舍五入 round(x)" }, { "dms", "小数度 → 度分秒 dms(x)" }, { "deg", "度分秒 → 小数度 deg(x)" } };
            for (int i = 0; i < functions.GetLength(0); i++) { string f = functions[i, 0]; AddMenu(menu, functions[i, 1], delegate { Wrap(f + "(", ")", false); }); }
            menu.Items.Add(new Separator());
            AddMenu(menu, "百分数 % · 除以 100", delegate { Wrap("(", ")%", true); });
            AddMenu(menu, "随机数 · 0 ≤ x < 1", delegate { Insert(ExpressionEngine.Literal(random.NextDouble())); });
            OpenMenu("FunctionButton", menu);
        }
        private void MemoryOperation(string operation)
        {
            double value; if (!Current(out value)) return;
            try
            {
                if (operation == "store") session.Store(value);
                else session.AddMemory(operation == "subtract" ? -value : value);
                UpdateMemory(); status.Text = "内存 = " + ExpressionEngine.Format(session.Memory, false); InvalidatePreview();
            }
            catch (CalculationException ex) { status.Text = ex.Message; }
        }
        private void UpdateMemory()
        {
            bool has = session.Memories.Count > 0;
            Find<Button>("MemoryClear").IsEnabled = has; Find<Button>("MemoryRecall").IsEnabled = has;
            Find<Button>("MemoryListButton").IsEnabled = has; Find<Button>("MemoryListButton").Content = has ? "M " + session.Memories.Count + " ▾" : "M ▾";
        }
        private void ShowMemory()
        {
            var menu = new ContextMenu();
            foreach (MemoryEntry entry in session.Memories)
            {
                double value = entry.Value; AddMenu(menu, entry.Display, delegate { InsertLiteral(ExpressionEngine.Literal(value)); });
            }
            OpenMenu("MemoryListButton", menu);
        }
        private void ExportHistory()
        {
            var dialog = new SaveFileDialog { Title = "导出本次会话", Filter = "文本文件 (*.txt)|*.txt", FileName = "计算记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
            if (dialog.ShowDialog(Window) == true) FileAction(delegate { File.WriteAllText(dialog.FileName, session.Export(), new UTF8Encoding(true)); status.Text = "记录已导出"; });
        }
        private void FileAction(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is FormatException) && !(ex is OverflowException)) throw;
                MessageBox.Show(Window, ex.Message, "无法完成操作", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        private void CommitGrid() { holdingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); holdingsGrid.CommitEdit(DataGridEditingUnit.Row, true); }
        private void UpdatePortfolio()
        {
            Find<TextBlock>("TotalCost").Text = portfolio.Valid ? Money.Display(portfolio.Cost) : "—";
            Find<TextBlock>("TotalValue").Text = portfolio.Valid ? Money.Display(portfolio.Value) : "—";
            Find<TextBlock>("TotalProfit").Text = portfolio.Valid ? Money.Display(portfolio.Profit) : "—";
            Find<TextBlock>("TotalReturn").Text = portfolio.Valid && portfolio.Return.HasValue ? Money.Display(portfolio.Return.Value) + "%" : "—";
            Find<TextBlock>("PortfolioStatus").Text = portfolio.Valid ? PortfolioHint : portfolio.Error;
            Find<TextBlock>("PortfolioStatus").Foreground = Brush(portfolio.Valid ? "#596578" : "#B42318");
        }
        private void ImportPortfolio()
        {
            var dialog = new OpenFileDialog { Title = "导入持仓（追加到当前组合）", Filter = "CSV 文件 (*.csv)|*.csv" };
            if (dialog.ShowDialog(Window) != true) return;
            FileAction(delegate
            {
                if (new FileInfo(dialog.FileName).Length > 4 * 1024 * 1024) throw new FormatException("CSV 文件最大支持 4 MB");
                List<Holding> rows = Portfolio.ImportCsv(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                CommitGrid(); portfolio.AddRange(rows);
                Find<TextBlock>("PortfolioStatus").Text = "已追加导入 " + rows.Count + " 条持仓。" + PortfolioHint;
            });
        }
        private void ExportPortfolio()
        {
            CommitGrid();
            if (!portfolio.Valid) { Find<TextBlock>("PortfolioStatus").Text = portfolio.Error; return; }
            var dialog = new SaveFileDialog { Title = "导出持仓与计算结果", Filter = "CSV 文件 (*.csv)|*.csv", FileName = "投资组合-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" };
            if (dialog.ShowDialog(Window) == true) FileAction(delegate { File.WriteAllText(dialog.FileName, portfolio.ExportCsv(), new UTF8Encoding(true)); Find<TextBlock>("PortfolioStatus").Text = "持仓已导出，可在下次打开时导入。"; });
        }
        private void RecordPortfolio()
        {
            CommitGrid();
            if (!portfolio.Valid || portfolio.Holdings.Count == 0) { Find<TextBlock>("PortfolioStatus").Text = "请先填写有效持仓。"; return; }
            var snapshot = new StringBuilder("组合净收益（" + portfolio.Holdings.Count + " 项）\n");
            snapshot.AppendLine("成本 " + Money.Exact(portfolio.Cost) + " · 市值 " + Money.Exact(portfolio.Value));
            snapshot.AppendLine("收益率 " + (portfolio.Return.HasValue ? Money.Exact(portfolio.Return.Value) + "%" : "—"));
            foreach (Holding item in portfolio.Holdings)
                snapshot.AppendLine(item.Name + "：" + item.QuantityText + " 份，均价 " + item.BuyPriceText + "，现价 " + item.CurrentPriceText + "，买费 " + item.BuyFeeText + "，卖费 " + item.SellFeeText);
            session.RecordFinancial(snapshot.ToString().TrimEnd(), portfolio.Profit); UpdateHistory();
            Find<TextBlock>("PortfolioStatus").Text = "组合快照已保存到本次会话计算历史 #" + session.History.Count + "，包含持仓明细和费用。";
        }
        private static readonly string[] AverageFields = { "AverageOldQuantity", "AverageOldCost", "AverageNewQuantity", "AverageNewPrice", "AverageFee" };
        private static readonly string[] AverageLabels = { "原持仓数量", "原每份成本", "补仓数量", "补仓价格", "本次费用" };
        private void Average(bool record)
        {
            try
            {
                decimal[] values = AverageFields.Select((name, i) => Money.Parse(Find<TextBox>(name).Text, AverageLabels[i])).ToArray();
                decimal average = Money.Average(values[0], values[1], values[2], values[3], values[4]);
                Find<TextBlock>("AverageResult").Text = "合计 " + Money.Exact(values[0] + values[2]) + " 份  ·  新成本 " + average.ToString("0.######", CultureInfo.InvariantCulture) + " / 份";
                if (record)
                {
                    string[] v = values.Select(Money.Exact).ToArray();
                    session.RecordFinancial("补仓每份成本\n(" + v[0] + " × " + v[1] + " + " + v[2] + " × " + v[3] + " + " + v[4] + ") / (" + v[0] + " + " + v[2] + ")", average);
                    UpdateHistory(); Find<TextBlock>("AverageResult").Text += "  ·  已记录 #" + session.History.Count;
                }
            }
            catch (FormatException ex) { Find<TextBlock>("AverageResult").Text = ex.Message; }
            catch (OverflowException) { Find<TextBlock>("AverageResult").Text = "数值过大，无法计算"; }
        }
        private void UseHolding()
        {
            CommitGrid(); Holding item = holdingsGrid.SelectedItem as Holding;
            if (item == null || !item.Valid || item.Quantity == 0) { Find<TextBlock>("AverageResult").Text = "请先选中数量大于零的有效持仓。"; return; }
            Find<TextBox>("AverageOldQuantity").Text = Money.Exact(item.Quantity);
            Find<TextBox>("AverageOldCost").Text = Money.Exact(item.Cost / item.Quantity);
        }
        private void ShowHelp()
        {
            MessageBox.Show(Window,
                "表达式编辑\n可直接输入、粘贴、选择和修改任意位置。Enter 或 = 计算并记录；Shift+Enter 换行；Esc 清空；Ctrl+L 选中表达式；Ctrl+Z 撤销；Ctrl+Y 重做。\n\n" +
                "函数与按键\n选中文字时，函数对选区生效。平方、根号等一元按键在未选择时作用于整个表达式；三角函数菜单在光标处插入。2ⁿᵈ 切换第二功能。^ 为乘方，% 固定表示除以 100；exp 按键插入 E（如 2.5E6）。函数 exp(x) 表示 e 的 x 次幂。\n\n" +
                "示例\n(128.6 - 112.4) * 300 - 8\nsin(30)^2 + cos(30)^2\nlog(8, 2) · root(27, 3) · 2π · 5!\n\n" +
                "历史与内存\n每次明确计算都保存，包含错误记录；预览不入历史。回填会恢复角度模式并固定原来的 Ans / mem。MS 可存多个值，MR / M+ / M− 操作最新一条。投资计算的回填带入结果。\n\n" +
                "投资组合\n同一币种、非负持仓；费用填写金额。收益率 = 净收益 / 含费成本，仓位 = 单项市值 / 总市值，保本卖价包含填写的预计卖出费。不自动拉取行情、税率或转换持仓币种；不统计分红和已实现收益。CSV 导入会追加持仓。\n\n" +
                "汇率转换\n选择币种并输入非负金额，即时换算。汇率来自 Frankfurter 的每日参考汇率，显示数据日期；刷新需联网，无需 API Key。缓存保留一小时，获取失败时明确标记使用的会话缓存。结果可复制、记入历史或带入科学计算，金额不会发送到服务端。\n\n" +
                "精度与保存\n科学计算使用约 15～16 位有效数字；投资页使用 decimal（28～29 位有效数字），金额仅在显示时保留两位。历史和持仓只在本次运行保留，关闭前可导出。",
                "清算 · 使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        public async System.Threading.Tasks.Task RunUiChecks(string directory)
        {
            Directory.CreateDirectory(directory);
            ReplaceAll("12+34"); editor.Select(3, 2); Insert("8");
            Assert(editor.Text == "12+8", "Selection replacement");
            editor.Undo(); Assert(editor.Text == "12+34", "Undo preserves expression");
            editor.Redo(); Assert(editor.Text == "12+8", "Redo");
            editor.Select(2, 0); Backspace(); Assert(editor.Text == "1+8", "Backspace at caret");
            editor.Undo(); Assert(editor.Text == "12+8", "Undo backspace");
            Calculate(); Assert(session.History[0].Value == 20, "Calculate and history");
            ReplaceAll("Ans*2"); Calculate(); HistoryEntry saved = session.History[0];
            ReplaceAll("99"); Calculate(); Restore(saved); Preview(); Assert(currentValue == 40, "Historical Ans context");
            ReplaceAll("25"); Preview(); MemoryOperation("store");
            ReplaceAll("5"); Preview(); MemoryOperation("add"); Assert(session.Memory == 30, "Memory addition");
            ReplaceAll("3+4"); Execute("square"); Preview(); Assert(currentValue == 49, "Unary expression wrapping");
            Execute("second"); Assert(Find<Button>("RootButton").Content.ToString() == "³√x", "Second functions"); Execute("second");
            ReplaceAll("1/0"); Calculate(); Assert(session.History[0].IsError, "Errors recorded");
            int count = session.History.Count; ReplaceAll(""); Assert(session.History.Count == count, "Clear keeps history");
            ReplaceAll("(128.6 - 112.4) × 300 - 8"); Calculate();
            ReplaceAll("sin(30)^2 + cos(30)^2"); Calculate();
            ReplaceAll("(128.6 - 112.4) × 300 - 8"); Preview();
            SaveScreenshot(Path.Combine(directory, "calculator.png"));
            Window.Width = 510; SetHistory(false); Window.UpdateLayout();
            Assert(Find<UniformGrid>("Keypad").ActualWidth > 400, "Compact calculator width");
            SaveScreenshot(Path.Combine(directory, "compact.png"));
            Window.Width = 1080;
            portfolio.Add(new Holding { Name = "指数基金 A", QuantityText = "1200", BuyPriceText = "3.25", CurrentPriceText = "3.68", BuyFeeText = "5", SellFeeText = "5" });
            portfolio.Add(new Holding { Name = "债券基金 B", QuantityText = "800", BuyPriceText = "1.08", CurrentPriceText = "1.12", BuyFeeText = "0", SellFeeText = "0" });
            portfolio.Add(new Holding { Name = "股票 C", QuantityText = "300", BuyPriceText = "112.4", CurrentPriceText = "128.6", BuyFeeText = "8", SellFeeText = "12" });
            ShowPage(true); holdingsGrid.SelectedIndex = 2; UseHolding();
            Find<TextBox>("AverageNewQuantity").Text = "100"; Find<TextBox>("AverageNewPrice").Text = "108"; Find<TextBox>("AverageFee").Text = "5";
            Average(true); RecordPortfolio();
            Assert(session.History[0].IsFinancial, "Financial history snapshot");
            Assert(Find<TextBlock>("TotalCost").Text == "38,497.00", "Portfolio totals bound");
            SaveScreenshot(Path.Combine(directory, "portfolio.png"));
            await RunCurrencyUiChecks(directory);
            File.WriteAllText(Path.Combine(directory, "ui-test-passed.txt"), "UI checks passed: edit, undo, redo, caret deletion, calculation, history restore, memory, second functions, errors, compact layout, portfolio bindings, financial snapshots, currency conversion, identity, invalid amounts, overflow, swap, result reuse, currency layouts, request races and failure recovery.\r\n", Encoding.UTF8);
        }
        private void SaveScreenshot(string path)
        {
            Window.UpdateLayout();
            // Capture only this application's content; no desktop capture is needed.
            FrameworkElement visual = (FrameworkElement)Window.Content;
            int width = (int)Math.Ceiling(visual.ActualWidth + visual.Margin.Left + visual.Margin.Right);
            int height = (int)Math.Ceiling(visual.ActualHeight + visual.Margin.Top + visual.Margin.Bottom);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (DrawingContext context = backdrop.RenderOpen()) context.DrawRectangle(Window.Background, null, new Rect(0, 0, width, height));
            bitmap.Render(backdrop);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
