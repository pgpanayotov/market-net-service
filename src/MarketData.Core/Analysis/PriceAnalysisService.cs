using MarketData.Contracts;
using MarketData.Core.Abstractions;
using MarketData.Core.Domain;

namespace MarketData.Core.Analysis;

public interface IPriceAnalysisService
{
    Task<AnalysisResult<AvgPriceResponse>> Get24hAverageAsync(string? symbol, CancellationToken cancellationToken);

    Task<AnalysisResult<SmaResponse>> GetSmaAsync(SmaQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Price analysis anchored at the newest stored data, not the wall clock (ADR-006):
/// <c>dataEnd = latestOpenTime + 1m</c>. A candle that has not been stored yet is never counted,
/// and removed symbols stay queryable.
/// </summary>
public sealed class PriceAnalysisService(
    ISymbolRepository symbols,
    ICandleQueryRepository candles,
    TimeProvider timeProvider) : IPriceAnalysisService
{
    public async Task<AnalysisResult<AvgPriceResponse>> Get24hAverageAsync(string? symbol, CancellationToken cancellationToken)
    {
        var errors = AnalysisValidator.ValidateSymbol(symbol, out var name);
        if (errors.Count > 0)
        {
            return new AnalysisError.Invalid(errors);
        }

        var (info, dataEnd, error) = await ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var windowStart = dataEnd - MarketDataConstants.AverageWindow;
        var average = await candles.GetAverageCloseAsync(info!.Id, windowStart, dataEnd, cancellationToken).ConfigureAwait(false);
        if (average is not { } avg)
        {
            return new AnalysisError.NoData(name.Value);
        }

        return new AvgPriceResponse
        {
            Symbol = name.Value,
            AveragePrice = avg.Average,
            From = avg.FirstOpenTime.UtcDateTime,
            To = dataEnd.UtcDateTime,
            DataPoints = avg.Count,
            IsFullPeriod = avg.FirstOpenTime <= windowStart,
        };
    }

    public async Task<AnalysisResult<SmaResponse>> GetSmaAsync(SmaQuery query, CancellationToken cancellationToken)
    {
        var errors = AnalysisValidator.Validate(query, timeProvider.GetUtcNow(), out var valid);
        if (valid is null)
        {
            return new AnalysisError.Invalid(errors);
        }

        var (info, dataEnd, error) = await ResolveAsync(valid.Symbol, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var anchor = valid.Start is { } start && start < dataEnd ? start : dataEnd;
        var referenceTime = valid.Period.Floor(anchor);
        var limit = valid.N + (valid.SeriesCount ?? 1) - 1;
        var buckets = await candles
            .GetBucketClosesAsync(info!.Id, valid.Period, referenceTime - valid.Period.Times(limit), referenceTime, limit, cancellationToken)
            .ConfigureAwait(false);

        var response = new SmaResponse
        {
            Symbol = valid.Symbol.Value,
            N = valid.N,
            Period = valid.Period.Code,
            ReferenceTime = referenceTime.UtcDateTime,
        };

        if (valid.SeriesCount is { } count)
        {
            var series = SmaCalculator.Series(buckets, valid.Period, valid.N, count, referenceTime);
            if (series.All(v => v.DataPointsUsed == 0))
            {
                return new AnalysisError.NoData(valid.Symbol.Value);
            }

            response.Series = [.. series.Select(v => new SmaSeriesItem
            {
                Time = v.Time.UtcDateTime,
                SimpleMovingAverage = v.Average,
                DataPointsUsed = v.DataPointsUsed,
            })];
            return response;
        }

        if (SmaCalculator.Average(buckets) is not { } sma)
        {
            return new AnalysisError.NoData(valid.Symbol.Value);
        }

        response.SimpleMovingAverage = sma;
        response.DataPointsUsed = buckets.Count;
        if (valid.IncludePoints)
        {
            response.Points = [.. buckets
                .OrderBy(b => b.BucketStart)
                .Select(b => new SmaPoint { Time = b.BucketStart.UtcDateTime, Close = b.Close })];
        }

        return response;
    }

    private async Task<(SymbolInfo? Info, DateTimeOffset DataEnd, AnalysisError? Error)> ResolveAsync(
        SymbolName name,
        CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(name, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return (null, default, new AnalysisError.UnknownSymbol(name.Value));
        }

        if (await candles.GetLatestOpenTimeAsync(info.Id, cancellationToken).ConfigureAwait(false) is not { } latest)
        {
            return (info, default, new AnalysisError.NoData(name.Value));
        }

        return (info, latest + MarketDataConstants.CandleInterval, null);
    }
}
