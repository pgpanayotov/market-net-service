using System.Runtime.Serialization;

namespace MarketData.Contracts;

/// <summary>Response of <c>GET /api/{symbol}/24hAvgPrice</c>.</summary>
[DataContract(Name = "avgPrice", Namespace = ContractNamespace.Value)]
public sealed class AvgPriceResponse
{
    [DataMember(Name = "symbol", Order = 1)]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Mean of the 1-minute closes in [<see cref="From"/>, <see cref="To"/>).</summary>
    [DataMember(Name = "averagePrice", Order = 2)]
    public decimal AveragePrice { get; set; }

    /// <summary>First candle used (inclusive, UTC).</summary>
    [DataMember(Name = "from", Order = 3)]
    public DateTime From { get; set; }

    /// <summary>End of the newest stored candle (exclusive, UTC). This is the window's anchor.</summary>
    [DataMember(Name = "to", Order = 4)]
    public DateTime To { get; set; }

    [DataMember(Name = "dataPoints", Order = 5)]
    public int DataPoints { get; set; }

    /// <summary><c>false</c> when less than 24 hours of history is available.</summary>
    [DataMember(Name = "isFullPeriod", Order = 6)]
    public bool IsFullPeriod { get; set; }
}
