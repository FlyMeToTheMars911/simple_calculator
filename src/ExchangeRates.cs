using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClearCalc
{
    public sealed class Currency
    {
        public string Code { get; private set; }
        public string Display { get; private set; }
        private Currency(string code, string name) { Code = code; Display = code + " · " + name; }
        public static readonly Currency[] Supported = {
            new Currency("CNY", "人民币"), new Currency("USD", "美元"), new Currency("EUR", "欧元"),
            new Currency("GBP", "英镑"), new Currency("JPY", "日元"), new Currency("HKD", "港币"),
            new Currency("TWD", "新台币"), new Currency("KRW", "韩元"), new Currency("AUD", "澳元"),
            new Currency("CAD", "加元"), new Currency("CHF", "瑞士法郎"), new Currency("SGD", "新加坡元"),
            new Currency("NZD", "新西兰元"), new Currency("THB", "泰铢"), new Currency("MYR", "马来西亚林吉特"),
            new Currency("INR", "印度卢比"), new Currency("IDR", "印度尼西亚盾"), new Currency("AED", "阿联酋迪拉姆")
        };
        public static void Validate(string code)
        {
            foreach (Currency currency in Supported) if (currency.Code == code) return;
            throw new FormatException("请选择支持的币种。");
        }
    }

    public sealed class ExchangeRate
    {
        public string Base { get; private set; }
        public string Quote { get; private set; }
        public string Date { get; private set; }
        public decimal Rate { get; private set; }
        public bool IsIdentity { get { return Base == Quote; } }
        public ExchangeRate(string from, string to, string date, decimal rate)
        {
            Currency.Validate(from); Currency.Validate(to);
            DateTime parsed;
            if (rate <= 0 || (from == to && rate != 1) || (from != to &&
                !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)))
                throw new FormatException("汇率数据无效，请稍后刷新。");
            Base = from; Quote = to; Date = date; Rate = rate;
        }
        public decimal ConvertAmount(decimal amount)
        {
            if (amount < 0) throw new FormatException("金额须为非负数（小数点用 .）。");
            return checked(amount * Rate);
        }
    }

    public sealed class ExchangeRateResult
    {
        public ExchangeRate Rate { get; private set; }
        public bool IsCached { get; private set; }
        public bool IsFallback { get; private set; }
        public DateTime FetchedAtUtc { get; private set; }
        public ExchangeRateResult(ExchangeRate rate, bool cached, bool fallback, DateTime fetchedAtUtc)
        { Rate = rate; IsCached = cached; IsFallback = fallback; FetchedAtUtc = fetchedAtUtc; }
    }

    public sealed class ExchangeRateService
    {
        private sealed class CachedRate { public ExchangeRate Rate; public DateTime FetchedAt; }
        [DataContract]
        private sealed class Response
        {
            [DataMember(Name = "date", IsRequired = true)] public string Date { get; set; }
            [DataMember(Name = "base", IsRequired = true)] public string Base { get; set; }
            [DataMember(Name = "quote", IsRequired = true)] public string Quote { get; set; }
            [DataMember(Name = "rate", IsRequired = true)] public decimal Rate { get; set; }
        }
        private static readonly HttpClient client = CreateClient();
        private readonly Dictionary<string, CachedRate> cache = new Dictionary<string, CachedRate>();
        private readonly Func<string, string, CancellationToken, Task<ExchangeRate>> fetch;
        private readonly Func<DateTime> utcNow;
        public ExchangeRateService() : this(FetchAsync, delegate { return DateTime.UtcNow; }) { }
        public ExchangeRateService(Func<string, string, CancellationToken, Task<ExchangeRate>> loader, Func<DateTime> clock)
        { fetch = loader; utcNow = clock; }
        private static HttpClient CreateClient()
        {
            var result = new HttpClient(); result.Timeout = TimeSpan.FromSeconds(15);
            result.DefaultRequestHeaders.UserAgent.ParseAdd("ClearCalc/1.1");
            return result;
        }
        public static ExchangeRate ParseResponse(string json, string from, string to)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var response = (Response)new DataContractJsonSerializer(typeof(Response)).ReadObject(stream);
                    if (response == null || response.Base != from || response.Quote != to)
                        throw new FormatException("返回的汇率币种不匹配。");
                    return new ExchangeRate(response.Base, response.Quote, response.Date, response.Rate);
                }
            }
            catch (SerializationException ex) { throw new FormatException("汇率服务返回了无法识别的数据。", ex); }
        }
        private static async Task<ExchangeRate> FetchAsync(string from, string to, CancellationToken token)
        {
            // Only currency codes are sent. Amounts and calculator data stay on this computer.
            string url = "https://api.frankfurter.dev/v2/rate/" + from + "/" + to;
            using (HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return ParseResponse(json, from, to);
            }
        }
        public async Task<ExchangeRateResult> GetAsync(string from, string to, bool refresh, CancellationToken token)
        {
            Currency.Validate(from); Currency.Validate(to); token.ThrowIfCancellationRequested();
            if (from == to) return new ExchangeRateResult(new ExchangeRate(from, to, "", 1m), false, false, utcNow());
            string key = from + "/" + to;
            CachedRate saved;
            lock (cache) cache.TryGetValue(key, out saved);
            TimeSpan age = saved == null ? TimeSpan.MaxValue : utcNow() - saved.FetchedAt;
            if (!refresh && saved != null && age >= TimeSpan.Zero && age < TimeSpan.FromHours(1))
                return new ExchangeRateResult(saved.Rate, true, false, saved.FetchedAt);
            try
            {
                ExchangeRate rate = await fetch(from, to, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (rate == null || rate.Base != from || rate.Quote != to) throw new FormatException("返回的汇率币种不匹配。");
                var entry = new CachedRate { Rate = rate, FetchedAt = utcNow() };
                lock (cache) cache[key] = entry;
                return new ExchangeRateResult(rate, false, false, entry.FetchedAt);
            }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                if (!(ex is HttpRequestException || ex is OperationCanceledException || ex is FormatException)) throw;
                if (saved == null) throw;
                return new ExchangeRateResult(saved.Rate, true, true, saved.FetchedAt);
            }
        }
    }
}
