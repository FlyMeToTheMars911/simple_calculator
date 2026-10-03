using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClearCalc;

public static class ExchangeRateTests
{
    private static async Task Throws<T>(Func<Task> action, Action<bool, string> check, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { check(true, message); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + message);
    }
    public static async Task Run(Action<bool, string> check)
    {
        const string json = "{\"date\":\"2026-10-02\",\"base\":\"USD\",\"quote\":\"CNY\",\"rate\":7.123456789012345678901234567}";
        ExchangeRate parsed = ExchangeRateService.ParseResponse(json, "USD", "CNY");
        check(parsed.Rate == 7.123456789012345678901234567m, "JSON preserves decimal rate precision");
        check(new ExchangeRate("USD", "CNY", "2026-10-02", 7.1m).ConvertAmount(0.1m) == 0.71m, "Exact decimal FX conversion");
        check(parsed.ConvertAmount(0) == 0, "Zero amount conversion");
        foreach (string invalid in new[] { "{}", "null", "<html>error</html>", json.Replace("7.123456789012345678901234567", "0"),
            json.Replace("7.123456789012345678901234567", "-1"), json.Replace("2026-10-02", "2026-02-30"), json.Replace("CNY", "EUR") })
            await Throws<FormatException>(delegate { ExchangeRateService.ParseResponse(invalid, "USD", "CNY"); return Task.FromResult(0); }, check, "Invalid API data rejected");
        await Throws<OverflowException>(delegate { parsed.ConvertAmount(Decimal.MaxValue); return Task.FromResult(0); }, check, "Conversion overflow");
        await Throws<FormatException>(delegate { parsed.ConvertAmount(-1); return Task.FromResult(0); }, check, "Negative conversion rejected");

        DateTime now = new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc);
        int requests = 0; bool offline = false, timeout = false;
        var service = new ExchangeRateService(delegate(string from, string to, CancellationToken token)
        {
            requests++;
            if (offline) throw new HttpRequestException("test offline");
            if (timeout) throw new TaskCanceledException("test timeout");
            return Task.FromResult(new ExchangeRate(from, to, "2026-10-02", from == "USD" ? 7.1m : 0.14m));
        }, delegate { return now; });
        var first = await service.GetAsync("USD", "CNY", false, CancellationToken.None);
        check(requests == 1 && !first.IsCached && !first.IsFallback && first.FetchedAtUtc == now, "Initial rate fetch");
        var cached = await service.GetAsync("USD", "CNY", false, CancellationToken.None);
        check(requests == 1 && cached.IsCached && !cached.IsFallback, "Fresh cache avoids HTTP");
        await service.GetAsync("USD", "CNY", true, CancellationToken.None);
        check(requests == 2, "Manual refresh bypasses cache");
        now = now.AddHours(1);
        await service.GetAsync("USD", "CNY", false, CancellationToken.None);
        check(requests == 3, "Expired cache refetched");
        var reverse = await service.GetAsync("CNY", "USD", false, CancellationToken.None);
        check(requests == 4 && reverse.Rate.Rate == 0.14m, "Directional caches are separate");
        offline = true;
        var fallback = await service.GetAsync("USD", "CNY", true, CancellationToken.None);
        check(fallback.IsCached && fallback.IsFallback && fallback.Rate.Rate == 7.1m, "Network failure uses labeled pair cache");
        await Throws<HttpRequestException>(async delegate { await service.GetAsync("EUR", "CNY", false, CancellationToken.None); }, check, "No unrelated cache on failure");
        int beforeIdentity = requests;
        var same = await service.GetAsync("CNY", "CNY", true, CancellationToken.None);
        check(requests == beforeIdentity && same.Rate.IsIdentity && same.Rate.ConvertAmount(12.34m) == 12.34m, "Same currency works offline");
        offline = false; timeout = true;
        check((await service.GetAsync("USD", "CNY", true, CancellationToken.None)).IsFallback, "Timeout uses labeled cache");
        await Throws<OperationCanceledException>(async delegate { await service.GetAsync("EUR", "CNY", false, CancellationToken.None); }, check, "Timeout without cache fails");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await Throws<OperationCanceledException>(async delegate { await service.GetAsync("USD", "CNY", false, cancel.Token); }, check, "Cancellation does not reuse cache");
        }
        var pending = new TaskCompletionSource<ExchangeRate>();
        var delayed = new ExchangeRateService(delegate { return pending.Task; }, delegate { return now; });
        using (var cancel = new CancellationTokenSource())
        {
            Task<ExchangeRateResult> request = delayed.GetAsync("USD", "CNY", false, cancel.Token);
            cancel.Cancel(); pending.SetResult(parsed);
            await Throws<OperationCanceledException>(async delegate { await request; }, check, "Late canceled response ignored");
        }
        await Throws<FormatException>(async delegate { await service.GetAsync("XYZ", "CNY", false, CancellationToken.None); }, check, "Invalid currency rejected");
        var session = new CalculatorSession();
        session.Calculate("10"); session.RecordFinancial("0.1 USD → CNY\n汇率日期 2026-10-02；来源 Frankfurter", 0.71m, true);
        check(session.History[0].Caption.Contains("汇率转换") && session.History[0].ExactValue == "0.71" && session.Answer == 10,
            "Conversion history preserves decimals and scientific answer");
        check(session.Export().Contains("来源 Frankfurter"), "Conversion history exports attribution");
    }
    public static async Task Smoke(Action<bool, string> check)
    {
        var service = new ExchangeRateService();
        var result = await service.GetAsync("USD", "CNY", true, CancellationToken.None);
        check(result.Rate.Rate > 0 && result.Rate.Base == "USD" && result.Rate.Quote == "CNY" && !result.IsFallback, "Live USD/CNY endpoint");
        Console.WriteLine("Frankfurter: 1 USD = " + Money.Exact(result.Rate.Rate) + " CNY; date " + result.Rate.Date);
    }
}
