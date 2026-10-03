using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClearCalc
{
    public sealed partial class MainController
    {
        private async Task WaitForCurrencyUi(Func<bool> ready)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert(ready(), "Currency UI continuation timed out");
        }
        private async Task RunCurrencyUiChecks(string directory)
        {
            ShowCurrencyPage();
            Assert(currencyVisible && !portfolioVisible && conversionValue == 710m, "Currency page loads a rate");
            Find<TextBox>("CurrencyAmount").Text = "0.1";
            Assert(conversionValue == 0.71m && Find<TextBox>("CurrencyResult").Text == "0.71", "Decimal conversion UI");
            foreach (string invalid in new[] { "", "bad", "-1", Decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            {
                Find<TextBox>("CurrencyAmount").Text = invalid;
                Assert(!conversionValue.HasValue && !Find<Button>("RecordConversionButton").IsEnabled &&
                    Find<TextBox>("CurrencyResult").Text == "—", "Invalid/overflow conversion clears previous result");
            }
            Find<TextBox>("CurrencyAmount").Text = "100";
            RecordConversion();
            Assert(session.History[0].IsCurrencyConversion && session.History[0].Expression.Contains("2026-10-02"), "Rate snapshot history");
            Find<TextBlock>("CurrencyFetchState").Text += "\n界面测试使用模拟汇率。";
            SaveScreenshot(Path.Combine(directory, "currency.png"));
            Window.Width = 510;
            SaveScreenshot(Path.Combine(directory, "currency-compact.png"));
            Assert(Find<ComboBox>("CurrencyFrom").ActualWidth > 140 && Find<ComboBox>("CurrencyTo").ActualWidth > 140,
                "Compact currency selectors remain usable");
            Find<Button>("SwapCurrenciesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(Find<ComboBox>("CurrencyFrom").SelectedValue.ToString() == "CNY" &&
                Find<ComboBox>("CurrencyTo").SelectedValue.ToString() == "USD" && Find<TextBox>("CurrencyAmount").Text == "100", "Currency swap keeps amount");
            Find<ComboBox>("CurrencyTo").SelectedValue = "CNY";
            Assert(conversionValue == 100m && displayedRate.Rate.IsIdentity, "Identity conversion without network");
            Find<Button>("UseConversionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!currencyVisible && editor.Text == "100", "Currency result reused in calculator");
            ReplaceAll("12+3"); Preview(); ShowCurrencyPage();
            Find<Button>("UseCalculatorAmountButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(Find<TextBox>("CurrencyAmount").Text == "15", "Calculator value used as amount");
            ShowPage(true); Assert(!currencyVisible && portfolioVisible, "Switch from currency to portfolio");

            // Simulate two overlapping requests; complete the obsolete pair last.
            ExchangeRateService original = exchangeRates;
            var yen = new TaskCompletionSource<ExchangeRate>();
            var euro = new TaskCompletionSource<ExchangeRate>();
            exchangeRates = new ExchangeRateService(delegate(string from, string to, System.Threading.CancellationToken token)
                { return from == "JPY" ? yen.Task : euro.Task; }, delegate { return DateTime.UtcNow; });
            ShowCurrencyPage(); // CNY/CNY still works without invoking the loader.
            Find<ComboBox>("CurrencyFrom").SelectedValue = "JPY";
            Assert(!conversionValue.HasValue, "New pair clears stale result while loading");
            Find<ComboBox>("CurrencyFrom").SelectedValue = "EUR";
            euro.SetResult(new ExchangeRate("EUR", "CNY", "2026-10-02", 8m));
            await WaitForCurrencyUi(delegate { return displayedRate != null; });
            Assert(displayedRate.Rate.Base == "EUR" && conversionValue == 120m, "Newest pair rendered");
            yen.SetResult(new ExchangeRate("JPY", "CNY", "2026-10-02", 0.05m));
            await Task.Delay(50);
            Assert(displayedRate.Rate.Base == "EUR" && conversionValue == 120m, "Late obsolete response cannot replace current pair");
            exchangeRates = new ExchangeRateService(delegate { throw new HttpRequestException("test offline"); }, delegate { return DateTime.UtcNow; });
            LoadExchangeRate(true);
            Assert(displayedRate == null && !conversionValue.HasValue && Find<TextBlock>("CurrencyFetchState").Text.Contains("无法获取"),
                "Network error shown without stale result");
            Find<ComboBox>("CurrencyFrom").SelectedValue = "CNY";
            Assert(conversionValue == 15m, "Identity recovers while offline");
            exchangeRates = original; ShowPage(false); Window.Width = 1080;
        }
    }
}
