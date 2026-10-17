using System.CommandLine;
using System.Net;
using MarketData.Client.Api;
using MarketData.Client.Output;
using MarketData.Contracts;

namespace MarketData.Client;

/// <summary>
/// The <c>marketdata</c> command line (P6): the same price analysis as the HTTP API, plus symbol
/// administration, by calling the API.
/// </summary>
/// <remarks>Exit codes: 0 success, 1 the API returned an error, 2 the API could not be reached or read, other non-zero values are usage errors.</remarks>
public static class Cli
{
    public const int Success = 0;
    public const int ApiError = 1;
    public const int ConnectionError = 2;

    public const string UrlVariable = "MARKETDATA_URL";
    public const string AdminKeyVariable = "MARKETDATA_ADMIN_KEY";
    public const string DefaultUrl = "http://localhost:8080";

    /// <param name="createHttpClient">Builds the HttpClient for a base URL; tests pass an in-memory API host here.</param>
    public static Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<Uri, HttpClient>? createHttpClient = null,
        CancellationToken cancellationToken = default)
    {
        var root = Build(output, error, createHttpClient ?? DefaultHttpClient);
        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }

    private static HttpClient DefaultHttpClient(Uri baseUrl) => new() { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };

    private static RootCommand Build(TextWriter output, TextWriter error, Func<Uri, HttpClient> createHttpClient)
    {
        var url = new Option<Uri>("--url")
        {
            Description = $"API base URL (default: ${UrlVariable} or {DefaultUrl})",
            Recursive = true,
            DefaultValueFactory = _ => new Uri(Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } value ? value : DefaultUrl),

            // System.CommandLine has no built-in Uri conversion.
            CustomParser = result =>
            {
                var text = result.Tokens.Count > 0 ? result.Tokens[^1].Value : string.Empty;
                if (Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
                {
                    return parsed;
                }

                result.AddError($"'{text}' is not an absolute http(s) URL.");
                return null;
            },
        };
        var format = new Option<ApiFormat>("--format", "-f")
        {
            Description = "Response format to request: json or xml (sets the Accept header)",
            Recursive = true,
            DefaultValueFactory = _ => ApiFormat.Json,
        };
        var raw = new Option<bool>("--raw")
        {
            Description = "Print the response body exactly as received instead of a table",
            Recursive = true,
        };
        var apiKey = new Option<string?>("--api-key")
        {
            Description = $"Admin API key for the symbols commands (default: ${AdminKeyVariable})",
            Recursive = true,
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable(AdminKeyVariable),
        };

        var root = new RootCommand("Query and administer the MarketData price analysis API.");
        root.Options.Add(url);
        root.Options.Add(format);
        root.Options.Add(raw);
        root.Options.Add(apiKey);

        root.Subcommands.Add(AverageCommand());
        root.Subcommands.Add(SmaCommand());
        root.Subcommands.Add(SymbolsCommand());
        return root;

        Command AverageCommand()
        {
            var symbol = SymbolArgument();
            var command = new Command("avg", "Average price over the last 24 hours of stored data (GET /api/{symbol}/24hAvgPrice)") { symbol };
            command.SetAction((parse, ct) => RunAsync(parse, async client =>
            {
                var response = await client.GetAverageAsync(parse.GetValue(symbol)!, ct).ConfigureAwait(false);
                if (!Raw(parse, response.RawBody))
                {
                    PrintAverage(response.Value);
                }
            }));
            return command;
        }

        Command SmaCommand()
        {
            var symbol = SymbolArgument();
            var n = new Option<int>("-n") { Description = "Number of data points (1-1000)", Required = true };
            var period = new Option<string>("-p", "--period") { Description = "Bucket size: 1m, 5m, 30m, 1d or 1w", Required = true };
            var start = new Option<string?>("-s", "--start") { Description = "Reference time, ISO-8601 (UTC when no offset; default: now)" };
            var points = new Option<bool>("--points") { Description = "Also list the bucket closes used (single value only)" };
            var series = new Option<bool>("--series") { Description = "Return a rolling series of SMA values" };
            var count = new Option<int?>("--count") { Description = "Series length (1-1000, default min(n, 100)); requires --series" };

            var command = new Command("sma", "Simple moving average (GET /api/{symbol}/SimpleMovingAverage)") { symbol, n, period, start, points, series, count };
            command.SetAction((parse, ct) => RunAsync(parse, async client =>
            {
                var response = await client.GetSmaAsync(
                    parse.GetValue(symbol)!,
                    parse.GetValue(n),
                    parse.GetValue(period)!,
                    parse.GetValue(start),
                    parse.GetValue(series),
                    parse.GetValue(count),
                    parse.GetValue(points),
                    ct).ConfigureAwait(false);
                if (!Raw(parse, response.RawBody))
                {
                    PrintSma(response.Value);
                }
            }));
            return command;
        }

        Command SymbolsCommand()
        {
            var list = new Command("list", "List monitored symbols, including removed ones (GET /api/admin/symbols)");
            list.SetAction((parse, ct) => RunAsync(parse, async client =>
            {
                var response = await client.ListSymbolsAsync(ct).ConfigureAwait(false);
                if (!Raw(parse, response.RawBody))
                {
                    PrintSymbols(response.Value);
                }
            }));

            var addSymbol = SymbolArgument();
            var add = new Command("add", "Start monitoring a symbol, or reactivate a removed one (POST /api/admin/symbols)") { addSymbol };
            add.SetAction((parse, ct) => RunAsync(parse, async client =>
            {
                var response = await client.AddSymbolAsync(parse.GetValue(addSymbol)!, ct).ConfigureAwait(false);
                if (!Raw(parse, response.RawBody))
                {
                    var verb = response.Status == HttpStatusCode.Created ? "Added" : "Reactivated";
                    output.WriteLine($"{verb} {response.Value.Name}. Ingestion starts within seconds; its history is backfilled.");
                }
            }));

            var removeSymbol = SymbolArgument();
            var remove = new Command("remove", "Stop monitoring a symbol; its history stays queryable (DELETE /api/admin/symbols/{symbol})") { removeSymbol };
            remove.SetAction((parse, ct) => RunAsync(parse, async client =>
            {
                var name = parse.GetValue(removeSymbol)!;
                await client.RemoveSymbolAsync(name, ct).ConfigureAwait(false);
                output.WriteLine($"Removed {name.ToUpperInvariant()}. Its history stays queryable.");
            }));

            return new Command("symbols", $"Administer monitored symbols (needs --api-key or ${AdminKeyVariable})") { list, add, remove };
        }

        async Task<int> RunAsync(ParseResult parse, Func<MarketDataApiClient, Task> action)
        {
            var baseUrl = parse.GetValue(url)!;
            using var http = createHttpClient(baseUrl);
            var client = new MarketDataApiClient(http, parse.GetValue(format), parse.GetValue(apiKey));
            try
            {
                await action(client).ConfigureAwait(false);
                return Success;
            }
            catch (ApiProblemException problem)
            {
                await error.WriteLineAsync($"Error {(int)problem.Status} {problem.Title}{(problem.Detail is null ? string.Empty : $": {problem.Detail}")}").ConfigureAwait(false);
                foreach (var (key, messages) in problem.Errors.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    await error.WriteLineAsync($"  {key}: {string.Join(" ", messages)}").ConfigureAwait(false);
                }

                if (problem.Status == HttpStatusCode.Unauthorized)
                {
                    await error.WriteLineAsync($"  Pass --api-key or set {AdminKeyVariable}.").ConfigureAwait(false);
                }

                return ApiError;
            }
            catch (HttpRequestException ex)
            {
                await error.WriteLineAsync($"Cannot reach the API at {baseUrl}: {ex.Message}").ConfigureAwait(false);
                return ConnectionError;
            }
            catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
            {
                await error.WriteLineAsync($"The API at {baseUrl} did not respond in time.").ConfigureAwait(false);
                return ConnectionError;
            }
            catch (InvalidDataException ex)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ConnectionError;
            }
        }

        bool Raw(ParseResult parse, string body)
        {
            if (!parse.GetValue(raw))
            {
                return false;
            }

            output.WriteLine(body);
            return true;
        }

        void PrintAverage(AvgPriceResponse avg)
        {
            output.WriteLine($"{avg.Symbol}  24h average price");
            TextTable.WriteFields(
                output,
                ("Average price", TextTable.Price(avg.AveragePrice)),
                ("Window", $"{TextTable.Time(avg.From)} -> {TextTable.Time(avg.To)}"),
                ("Data points", $"{TextTable.Number(avg.DataPoints)}{(avg.IsFullPeriod ? " (full 24h)" : " (less than 24h of data available)")}"));
        }

        void PrintSma(SmaResponse sma)
        {
            if (sma.Series is { } values)
            {
                output.WriteLine($"{sma.Symbol}  rolling SMA({sma.N}, {sma.Period}), {values.Count} value(s) up to {TextTable.Time(sma.ReferenceTime)}");
                TextTable.Write(
                    output,
                    ["Window end", "SMA", "Points"],
                    values.Select(v => (IReadOnlyList<string>)[TextTable.Time(v.Time), TextTable.Price(v.SimpleMovingAverage), TextTable.Number(v.DataPointsUsed)]),
                    rightAligned: new HashSet<int> { 1, 2 });
                return;
            }

            output.WriteLine($"{sma.Symbol}  SMA({sma.N}, {sma.Period}) as of {TextTable.Time(sma.ReferenceTime)}");
            TextTable.WriteFields(
                output,
                ("SMA", sma.SimpleMovingAverage is { } value ? TextTable.Price(value) : "-"),
                ("Data points", $"{TextTable.Number(sma.DataPointsUsed ?? 0)} of {TextTable.Number(sma.N)}"));
            if (sma.Points is { Count: > 0 } points)
            {
                output.WriteLine();
                TextTable.Write(
                    output,
                    ["Bucket start", "Close"],
                    points.Select(p => (IReadOnlyList<string>)[TextTable.Time(p.Time), TextTable.Price(p.Close)]),
                    rightAligned: new HashSet<int> { 1 });
            }
        }

        void PrintSymbols(SymbolList symbols)
        {
            TextTable.Write(
                output,
                ["Symbol", "Active", "Created", "Last candle"],
                symbols.Select(s => (IReadOnlyList<string>)[s.Name, s.IsActive ? "yes" : "no", TextTable.Time(s.CreatedAt), s.LastCandleAt is { } last ? TextTable.Time(last) : "-"]));
        }
    }

    private static Argument<string> SymbolArgument() => new("symbol") { Description = "Trading pair, e.g. BTCUSDT (case-insensitive)" };
}
