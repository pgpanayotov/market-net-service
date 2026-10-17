namespace MarketData.Contracts;

/// <summary>
/// XML namespace for all data contracts. It is empty so XML responses have plain element names
/// (<c>&lt;avgPrice&gt;&lt;symbol&gt;…</c>) that match the camelCase JSON property names.
/// </summary>
/// <remarks>
/// DTO timestamps are UTC <see cref="DateTime"/> values rather than <see cref="DateTimeOffset"/>.
/// <c>DataContractSerializer</c> writes a <see cref="DateTimeOffset"/> as a nested
/// <c>&lt;DateTime&gt;/&lt;OffsetMinutes&gt;</c> element, whereas a UTC <see cref="DateTime"/>
/// is written as <c>2026-10-18T12:05:00Z</c> in both JSON and XML.
/// </remarks>
public static class ContractNamespace
{
    public const string Value = "";
}
