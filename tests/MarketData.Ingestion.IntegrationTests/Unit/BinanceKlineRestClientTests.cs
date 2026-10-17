using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.IntegrationTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarketData.Ingestion.IntegrationTests.Unit;

/// <summary><see cref="BinanceKlineRestClient"/> through a real Binance.Net client against a stubbed <c>/api/v3/klines</c>.</summary>
public sealed class BinanceKlineRestClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 30, TimeSpan.Zero);
    private static readonly SymbolInfo Eth = new(3, SymbolName.TryCreate("ETHUSDT", out var s) ? s : default, IsActive: true);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _http = new();

    public BinanceKlineRestClientTests() => _http.Fallback = ServeKlines;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Long_range_is_paged_at_1000_klines_per_request_without_gaps_or_overlap()
    {
        var from = Now.AddMinutes(-2500.5);
        var to = from.AddMinutes(2500);

        var pages = await Client().GetClosedCandlesAsync(Eth, new TimeRange(from, to), Ct).ToListAsync(Ct);

        Assert.Equal([1000, 1000, 500], pages.Select(p => p.Count));
        Assert.Equal([1000, 1000, 500], _http.Requests.Select(u => int.Parse(Query(u)["limit"]!, CultureInfo.InvariantCulture)));
        var times = pages.SelectMany(p => p).Select(c => c.OpenTime).ToList();
        Assert.Equal(Floor(from), times[0]);
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.Equal(TimeSpan.FromMinutes(1), pair.Second - pair.First));
        Assert.All(_http.Requests, u => Assert.Equal("ETHUSDT", Query(u)["symbol"]));
    }

    [Fact]
    public async Task The_still_open_minute_is_never_returned()
    {
        var pages = await Client().GetClosedCandlesAsync(Eth, new TimeRange(Now.AddMinutes(-3), Now.AddMinutes(5)), Ct).ToListAsync(Ct);

        var times = pages.SelectMany(p => p).Select(c => c.OpenTime).ToList();
        Assert.Equal(Floor(Now).AddMinutes(-1), times[^1]);
    }

    [Fact]
    public async Task Http_429_waits_for_retry_after_then_resumes()
    {
        var requestTimes = new List<DateTimeOffset>();
        _http.OnRequest = _ => requestTimes.Add(_time.GetUtcNow());
        _http.Enqueue(_ => RateLimited(HttpStatusCode.TooManyRequests, retryAfterSeconds: 7));

        var pages = await DriveAsync(Client().GetClosedCandlesAsync(Eth, new TimeRange(Now.AddMinutes(-10), Now.AddMinutes(-5)), Ct).ToListAsync(Ct).AsTask());

        Assert.Equal(5, pages.Sum(p => p.Count));
        Assert.Equal(2, requestTimes.Count);
        Assert.True(requestTimes[1] - requestTimes[0] >= TimeSpan.FromSeconds(7), $"retried after {requestTimes[1] - requestTimes[0]}");
    }

    [Fact]
    public async Task Http_418_ban_blocks_later_calls_until_it_expires()
    {
        var requestTimes = new List<DateTimeOffset>();
        _http.OnRequest = _ => requestTimes.Add(_time.GetUtcNow());
        _http.Enqueue(_ => RateLimited((HttpStatusCode)418, retryAfterSeconds: 120));
        var client = Client();

        await DriveAsync(client.GetClosedCandlesAsync(Eth, new TimeRange(Now.AddMinutes(-10), Now.AddMinutes(-9)), Ct).ToListAsync(Ct).AsTask());
        var banStart = requestTimes[0];
        await client.GetClosedCandlesAsync(Eth, new TimeRange(Now.AddMinutes(-9), Now.AddMinutes(-8)), Ct).ToListAsync(Ct);

        Assert.Equal(3, requestTimes.Count);
        Assert.All(requestTimes.Skip(1), t => Assert.True(t - banStart >= TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public async Task Permanent_errors_are_not_retried()
    {
        _http.EnqueueJson("""{"code":-1121,"msg":"Invalid symbol."}""", HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<KlineRequestException>(async () =>
            await Client().GetClosedCandlesAsync(Eth, new TimeRange(Now.AddMinutes(-10), Now.AddMinutes(-5)), Ct).ToListAsync(Ct).AsTask());
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Empty_range_sends_no_request()
    {
        var pages = await Client().GetClosedCandlesAsync(Eth, new TimeRange(Now.AddMinutes(-5), Now.AddMinutes(-5)), Ct).ToListAsync(Ct);

        Assert.Empty(pages);
        Assert.Empty(_http.Requests);
    }

    private BinanceKlineRestClient Client() =>
        new(BinanceFixtures.RestClient(_http), _time, NullLogger<BinanceKlineRestClient>.Instance);

    /// <summary>Advances fake time while <paramref name="task"/> waits on it (Retry-After delays).</summary>
    private async Task<T> DriveAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 10_000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, Ct);
        }

        return await task;
    }

    private static HttpResponseMessage RateLimited(HttpStatusCode status, int retryAfterSeconds)
    {
        var response = StubHttpHandler.Json($$"""{"code":-1003,"msg":"Too many requests."}""", status);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    /// <summary>Binance's behaviour: klines from <c>startTime</c>, at most <c>limit</c>, none after <c>endTime</c> or the current minute.</summary>
    private HttpResponseMessage ServeKlines(HttpRequestMessage request)
    {
        var query = Query(request.RequestUri!);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(query["startTime"]!, CultureInfo.InvariantCulture));
        var end = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(query["endTime"]!, CultureInfo.InvariantCulture));
        var limit = int.Parse(query["limit"]!, CultureInfo.InvariantCulture);
        var json = new StringBuilder("[");
        var open = start.ToUnixTimeMilliseconds() % 60_000 == 0 ? start : Floor(start).AddMinutes(1);
        for (var n = 0; n < limit && open <= end && open <= _time.GetUtcNow(); n++, open = open.AddMinutes(1))
        {
            var ms = open.ToUnixTimeMilliseconds();
            json.Append(CultureInfo.InvariantCulture, $"""{(n > 0 ? "," : "")}[{ms},"1.0","2.0","0.5","1.5","10.0",{ms + 59_999},"15.0",{n + 1},"5.0","7.5","0"]""");
        }

        return StubHttpHandler.Json(json.Append(']').ToString());
    }

    private static System.Collections.Specialized.NameValueCollection Query(Uri uri) => HttpUtility.ParseQueryString(uri.Query);

    private static DateTimeOffset Floor(DateTimeOffset t) => new(t.UtcTicks - (t.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
}
