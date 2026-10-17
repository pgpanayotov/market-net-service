using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace MarketData.Contracts;

/// <summary>
/// Response of <c>GET /api/{symbol}/SimpleMovingAverage</c>. A single-value response sets
/// <see cref="SimpleMovingAverage"/>, <see cref="DataPointsUsed"/> and optionally <see cref="Points"/>.
/// A series response (<c>series=true</c>) sets only <see cref="Series"/>. Unset members are omitted
/// from both JSON and XML.
/// </summary>
[DataContract(Name = "sma", Namespace = ContractNamespace.Value)]
public sealed class SmaResponse
{
    [DataMember(Name = "symbol", Order = 1)]
    public string Symbol { get; set; } = string.Empty;

    [DataMember(Name = "n", Order = 2)]
    public int N { get; set; }

    /// <summary>Period code: <c>1m</c>, <c>5m</c>, <c>30m</c>, <c>1d</c> or <c>1w</c>.</summary>
    [DataMember(Name = "period", Order = 3)]
    public string Period { get; set; } = string.Empty;

    /// <summary>Exclusive end of the newest bucket used (UTC, aligned to the period).</summary>
    [DataMember(Name = "referenceTime", Order = 4)]
    public DateTime ReferenceTime { get; set; }

    [DataMember(Name = "simpleMovingAverage", Order = 5, EmitDefaultValue = false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SimpleMovingAverage { get; set; }

    /// <summary>Number of buckets that had data; less than <see cref="N"/> when there are gaps or little history.</summary>
    [DataMember(Name = "dataPointsUsed", Order = 6, EmitDefaultValue = false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DataPointsUsed { get; set; }

    /// <summary>The bucket closes the average was computed from, oldest first.</summary>
    [DataMember(Name = "points", Order = 7, EmitDefaultValue = false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SmaPoint>? Points { get; set; }

    /// <summary>
    /// Exactly <c>count</c> rolling SMA values, oldest first; the last one equals the single-value result.
    /// Windows without data are present with <c>dataPointsUsed</c> 0 and no average.
    /// </summary>
    [DataMember(Name = "series", Order = 8, EmitDefaultValue = false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SmaSeriesItem>? Series { get; set; }
}

/// <summary>Close price of one bucket.</summary>
[DataContract(Name = "point", Namespace = ContractNamespace.Value)]
public sealed class SmaPoint
{
    /// <summary>Bucket start (UTC).</summary>
    [DataMember(Name = "time", Order = 1)]
    public DateTime Time { get; set; }

    [DataMember(Name = "close", Order = 2)]
    public decimal Close { get; set; }
}

/// <summary>One value of a rolling SMA series.</summary>
[DataContract(Name = "item", Namespace = ContractNamespace.Value)]
public sealed class SmaSeriesItem
{
    /// <summary>Exclusive end of this value's window (UTC).</summary>
    [DataMember(Name = "time", Order = 1)]
    public DateTime Time { get; set; }

    /// <summary>Omitted when the window has no data (<see cref="DataPointsUsed"/> is 0).</summary>
    [DataMember(Name = "simpleMovingAverage", Order = 2, EmitDefaultValue = false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SimpleMovingAverage { get; set; }

    [DataMember(Name = "dataPointsUsed", Order = 3)]
    public int DataPointsUsed { get; set; }
}
