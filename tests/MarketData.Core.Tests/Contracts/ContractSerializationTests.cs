using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Xml;
using MarketData.Contracts;
using static MarketData.Core.Tests.TestTime;

namespace MarketData.Core.Tests.Contracts;

/// <summary>
/// The API serializes DTOs with System.Text.Json (web defaults) and DataContractSerializer (ADR-004),
/// so every contract must round-trip through both and produce stable element names.
/// </summary>
public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DateTime Ref = At("2026-10-18T12:05:00Z").UtcDateTime;

    public static TheoryData<object> Samples() =>
    [
        new AvgPriceResponse { Symbol = "BTCUSDT", AveragePrice = 67412.5300000001m, From = Ref.AddDays(-1), To = Ref, DataPoints = 1440, IsFullPeriod = true },
        SingleValueSma(),
        SeriesSma(),
        new SymbolList([new SymbolDto { Name = "BTCUSDT", IsActive = true, CreatedAt = Ref, LastCandleAt = Ref }, new SymbolDto { Name = "ADAUSDT", CreatedAt = Ref }]),
        new AddSymbolRequest { Symbol = "SOLUSDT" },
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void RoundTripsThroughJson(object dto)
    {
        var json = JsonSerializer.Serialize(dto, dto.GetType(), Json);
        var back = JsonSerializer.Deserialize(json, dto.GetType(), Json);

        Assert.Equal(json, JsonSerializer.Serialize(back, dto.GetType(), Json));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void RoundTripsThroughXml(object dto)
    {
        var xml = ToXml(dto);
        var back = FromXml(xml, dto.GetType());

        Assert.Equal(xml, ToXml(back));
    }

    [Fact]
    public void SingleValueSma_Xml_HasPlainNamesAndOmitsUnsetMembers()
    {
        var xml = ToXml(SingleValueSma());

        Assert.StartsWith("<sma xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">", xml, StringComparison.Ordinal);
        Assert.Contains("<referenceTime>2026-10-18T12:05:00Z</referenceTime>", xml, StringComparison.Ordinal);
        Assert.Contains("<simpleMovingAverage>67390.11</simpleMovingAverage>", xml, StringComparison.Ordinal);
        Assert.Contains("<points><point><time>2026-10-18T12:00:00Z</time><close>67301.0</close></point></points>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("series", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("i:nil", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesSma_Json_OmitsSingleValueMembers()
    {
        var json = JsonSerializer.Serialize(SeriesSma(), Json);

        Assert.Equal(
            """{"symbol":"BTCUSDT","n":20,"period":"5m","referenceTime":"2026-10-18T12:05:00Z","series":[{"time":"2026-10-18T12:05:00Z","simpleMovingAverage":67390.11,"dataPointsUsed":20}]}""",
            json);
    }

    [Fact]
    public void EmptySeriesWindow_OmitsTheAverage_InJsonAndXml()
    {
        var sma = SeriesSma();
        sma.Series = [new SmaSeriesItem { Time = Ref, SimpleMovingAverage = null, DataPointsUsed = 0 }];

        Assert.Contains("""{"time":"2026-10-18T12:05:00Z","dataPointsUsed":0}""", JsonSerializer.Serialize(sma, Json), StringComparison.Ordinal);
        var xml = ToXml(sma);
        Assert.Contains("<series><item><time>2026-10-18T12:05:00Z</time><dataPointsUsed>0</dataPointsUsed></item></series>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("i:nil", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolList_Xml_UsesNamedCollection_AndJsonIsAnArray()
    {
        var list = new SymbolList([new SymbolDto { Name = "ETHUSDT", IsActive = true, CreatedAt = Ref }]);

        Assert.StartsWith("<symbols", ToXml(list), StringComparison.Ordinal);
        Assert.Contains("<symbol><name>ETHUSDT</name>", ToXml(list), StringComparison.Ordinal);
        Assert.StartsWith("[{\"name\":\"ETHUSDT\"", JsonSerializer.Serialize(list, Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_Deserialization_KeepsUtcKind()
    {
        var back = (AvgPriceResponse)FromXml(ToXml(new AvgPriceResponse { From = Ref, To = Ref }), typeof(AvgPriceResponse));

        Assert.Equal(DateTimeKind.Utc, back.To.Kind);
        Assert.Equal(Ref, back.To);
    }

    private static SmaResponse SingleValueSma() => new()
    {
        Symbol = "BTCUSDT",
        N = 20,
        Period = "5m",
        ReferenceTime = Ref,
        SimpleMovingAverage = 67390.11m,
        DataPointsUsed = 20,
        Points = [new SmaPoint { Time = Ref.AddMinutes(-5), Close = 67301.0m }],
    };

    private static SmaResponse SeriesSma() => new()
    {
        Symbol = "BTCUSDT",
        N = 20,
        Period = "5m",
        ReferenceTime = Ref,
        Series = [new SmaSeriesItem { Time = Ref, SimpleMovingAverage = 67390.11m, DataPointsUsed = 20 }],
    };

    private static string ToXml(object dto)
    {
        var serializer = new DataContractSerializer(dto.GetType());
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            serializer.WriteObject(writer, dto);
        }

        return sb.ToString();
    }

    private static object FromXml(string xml, Type type)
    {
        using var reader = XmlReader.Create(new StringReader(xml));
        return new DataContractSerializer(type).ReadObject(reader)!;
    }
}
