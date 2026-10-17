using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace MarketData.Contracts;

/// <summary>A monitored symbol, as returned by <c>GET /api/admin/symbols</c>.</summary>
[DataContract(Name = "symbol", Namespace = ContractNamespace.Value)]
public sealed class SymbolDto
{
    [DataMember(Name = "name", Order = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary><c>false</c> after removal; history stays queryable.</summary>
    [DataMember(Name = "isActive", Order = 2)]
    public bool IsActive { get; set; }

    [DataMember(Name = "createdAt", Order = 3)]
    public DateTime CreatedAt { get; set; }

    /// <summary>Open time of the newest stored candle; absent until ingestion has started.</summary>
    [DataMember(Name = "lastCandleAt", Order = 4, EmitDefaultValue = false)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastCandleAt { get; set; }
}

/// <summary>
/// List of symbols. A named collection keeps XML readable
/// (<c>&lt;symbols&gt;&lt;symbol&gt;…</c> rather than <c>&lt;ArrayOfsymbol&gt;</c>), while JSON is a plain array.
/// </summary>
[CollectionDataContract(Name = "symbols", ItemName = "symbol", Namespace = ContractNamespace.Value)]
public sealed class SymbolList : List<SymbolDto>
{
    public SymbolList()
    {
    }

    public SymbolList(IEnumerable<SymbolDto> items)
        : base(items)
    {
    }
}

/// <summary>Body of <c>POST /api/admin/symbols</c>.</summary>
[DataContract(Name = "addSymbol", Namespace = ContractNamespace.Value)]
public sealed class AddSymbolRequest
{
    [DataMember(Name = "symbol", Order = 1)]
    public string Symbol { get; set; } = string.Empty;
}
