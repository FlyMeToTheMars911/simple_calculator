using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ClearCalc
{
    public sealed partial class MainController
    {
        private ExchangeRateService exchangeRates;
        private ExchangeRateResult displayedRate;
        private CancellationTokenSource rateRequest;
        private bool currencyVisible, changingCurrencies, currencyRequested;
        private decimal? conversionValue;
        private string conversionDescription;
        private string SelectedCurrency(string name)
        {
            Currency selected = Find<ComboBox>(name).SelectedItem as Currency;
            return selected == null ? null : selected.Code;
        }

        private void InitializeCurrencyPage(ExchangeRateService rates)
        {
            exchangeRates = rates;
            ComboBox from = Find<ComboBox>("CurrencyFrom"), to = Find<ComboBox>("CurrencyTo");
            from.ItemsSource = Currency.Supported; to.ItemsSource = Currency.Supported;
            from.SelectedValue = "USD"; to.SelectedValue = "CNY";
            from.SelectionChanged += delegate { if (!changingCurrencies && currencyRequested) LoadExchangeRate(false); };
            to.SelectionChanged += delegate { if (!changingCurrencies && currencyRequested) LoadExchangeRate(false); };
            Find<TextBox>("CurrencyAmount").TextChanged += delegate { UpdateConversion(); };
            On("CurrencyTab", ShowCurrencyPage);
            On("SwapCurrenciesButton", delegate
            {
                changingCurrencies = true;
                object old = from.SelectedValue; from.SelectedValue = to.SelectedValue; to.SelectedValue = old;
                changingCurrencies = false; LoadExchangeRate(false);
            });
            On("RefreshRatesButton", delegate { LoadExchangeRate(true); });
            On("CopyConversionButton", CopyConversion);
            On("RecordConversionButton", RecordConversion);
            On("UseConversionButton", delegate
            {
                if (!conversionValue.HasValue) return;
                string value = Money.Exact(conversionValue.Value);
                ShowPage(false); ReplaceAll(value);
                status.Text = "已带入汇率转换结果；科学计算使用约 15 位有效数字";
            });
            On("UseCalculatorAmountButton", delegate
            {
                double value;
                if (!Current(out value)) { SetAmountState(lastPreviewError ?? "请先在科学计算页计算有效结果。", true); return; }
                if (value < 0) { SetAmountState("待转换金额须为非负数。", true); return; }
                try { Find<TextBox>("CurrencyAmount").Text = Money.Exact((decimal)value); }
                catch (OverflowException) { SetAmountState("科学计算结果超出十进制金额范围。", true); }
            });
            Window.Closed += delegate { if (rateRequest != null) rateRequest.Cancel(); };
        }
        private void ShowCurrencyPage()
        {
            currencyVisible = true; portfolioVisible = false; Window.MinWidth = 500;
            Find<Grid>("CalculatorPage").Visibility = Visibility.Collapsed;
            Find<Grid>("PortfolioPage").Visibility = Visibility.Collapsed;
            Find<ScrollViewer>("CurrencyPage").Visibility = Visibility.Visible;
            Find<Button>("CalculatorTab").Style = (Style)Window.Resources[typeof(Button)];
            Find<Button>("PortfolioTab").Style = (Style)Window.Resources[typeof(Button)];
            Find<Button>("CurrencyTab").Style = (Style)Window.Resources["Accent"];
            // Opening the page checks the session cache; no requests on application startup.
            LoadExchangeRate(false);
            Find<TextBox>("CurrencyAmount").Focus();
        }
        private async void LoadExchangeRate(bool refresh)
        {
            // SelectedItem is already updated during SelectionChanged; SelectedValue can still be the previous value.
            string from = SelectedCurrency("CurrencyFrom");
            string to = SelectedCurrency("CurrencyTo");
            if (from == null || to == null) return;
            currencyRequested = true;
            if (rateRequest != null) rateRequest.Cancel();
            var request = new CancellationTokenSource(); rateRequest = request;
            displayedRate = null; UpdateConversion();
            Find<TextBlock>("CurrencyRateLine").Text = "正在获取 " + from + " → " + to + " 汇率…";
            SetFetchState("正在获取参考汇率…", false);
            Find<Button>("RefreshRatesButton").IsEnabled = false;
            try
            {
                ExchangeRateResult result = await exchangeRates.GetAsync(from, to, refresh, request.Token);
                if (request.IsCancellationRequested || rateRequest != request) return;
                displayedRate = result;
                ExchangeRate rate = result.Rate;
                Find<TextBlock>("CurrencyRateLine").Text = "1 " + from + " = " + Money.Exact(rate.Rate) + " " + to +
                    (rate.IsIdentity ? " · 同一币种" : "\n汇率日期：" + rate.Date);
                string fetched = result.FetchedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                SetFetchState(rate.IsIdentity ? "同一币种按 1:1 换算，无需联网。" :
                    (result.IsFallback ? "获取失败，正在使用本次运行的缓存；可稍后刷新。" :
                    result.IsCached ? "使用本次运行缓存（有效期 1 小时）；可手动刷新。" : "已获取 Frankfurter 参考汇率。") +
                    "\n最近成功获取：" + fetched, result.IsFallback);
                UpdateConversion();
            }
            catch (OperationCanceledException)
            {
                if (!request.IsCancellationRequested && rateRequest == request) RateFailed("获取汇率超时，请检查网络后刷新。");
            }
            catch (HttpRequestException)
            {
                if (!request.IsCancellationRequested && rateRequest == request) RateFailed("无法获取汇率，请检查网络或稍后刷新。");
            }
            catch (FormatException ex)
            {
                if (!request.IsCancellationRequested && rateRequest == request) RateFailed(ex.Message);
            }
            finally
            {
                if (rateRequest == request) { Find<Button>("RefreshRatesButton").IsEnabled = true; rateRequest = null; }
                request.Dispose();
            }
        }
        private void RateFailed(string message)
        {
            displayedRate = null; Find<TextBlock>("CurrencyRateLine").Text = "汇率暂不可用";
            SetFetchState(message, true); UpdateConversion();
        }
        private void SetFetchState(string text, bool error)
        {
            Find<TextBlock>("CurrencyFetchState").Text = text;
            Find<TextBlock>("CurrencyFetchState").Foreground = Brush(error ? "#B42318" : "#596578");
        }
        private void SetAmountState(string text, bool error)
        {
            Find<TextBlock>("CurrencyAmountState").Text = text;
            Find<TextBlock>("CurrencyAmountState").Foreground = Brush(error ? "#B42318" : "#6D7888");
        }
        private void UpdateConversion()
        {
            conversionValue = null; conversionDescription = null;
            Find<TextBox>("CurrencyResult").Text = "—";
            string target = SelectedCurrency("CurrencyTo");
            Find<TextBlock>("CurrencyResultLabel").Text = "换算结果 · " + target;
            try
            {
                string text = Find<TextBox>("CurrencyAmount").Text;
                if (String.IsNullOrWhiteSpace(text)) SetAmountState("请输入待转换金额。", false);
                else
                {
                    decimal amount = Money.Parse(text, "金额");
                    SetAmountState("十进制计算，结果保留可用精度；支持科学计数法。", false);
                    if (displayedRate != null)
                    {
                        ExchangeRate rate = displayedRate.Rate;
                        decimal value = rate.ConvertAmount(amount);
                        conversionValue = value;
                        Find<TextBox>("CurrencyResult").Text = Money.Exact(value);
                        conversionDescription = Money.Exact(amount) + " " + rate.Base + " → " + rate.Quote +
                            "\n1 " + rate.Base + " = " + Money.Exact(rate.Rate) + " " + rate.Quote +
                            (rate.IsIdentity ? "；同一币种" : "；汇率日期 " + rate.Date + "；来源 Frankfurter" +
                            "；获取于 " + displayedRate.FetchedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) +
                            (displayedRate.IsFallback ? "；获取失败，使用会话缓存" : displayedRate.IsCached ? "；会话缓存" : "");
                    }
                }
            }
            catch (FormatException ex) { SetAmountState(ex.Message, true); }
            catch (OverflowException) { SetAmountState("换算结果超出十进制金额范围，请减小金额。", true); }
            foreach (string button in new[] { "RecordConversionButton", "CopyConversionButton", "UseConversionButton" })
                Find<Button>(button).IsEnabled = conversionValue.HasValue;
        }
        private void CopyConversion()
        {
            if (!conversionValue.HasValue) return;
            Copy(Money.Exact(conversionValue.Value)); SetAmountState(status.Text, false);
        }
        private void RecordConversion()
        {
            if (!conversionValue.HasValue) return;
            HistoryEntry entry = session.RecordFinancial(conversionDescription, conversionValue.Value, true);
            UpdateHistory(); SetAmountState("已保存到历史 #" + entry.Number + "，包含币种、汇率和数据日期。", false);
        }
    }
}
