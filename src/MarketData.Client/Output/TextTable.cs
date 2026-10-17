using System.Globalization;

namespace MarketData.Client.Output;

/// <summary>Plain-text output helpers: aligned tables and invariant number/date formats.</summary>
internal static class TextTable
{
    public static void Write(TextWriter output, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, ISet<int>? rightAligned = null)
    {
        var all = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length))).ToArray();

        output.WriteLine(Line(headers));
        output.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in all)
        {
            output.WriteLine(Line(row));
        }

        string Line(IReadOnlyList<string> cells) =>
            string.Join("  ", cells.Select((c, i) => rightAligned?.Contains(i) == true ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();
    }

    /// <summary>Key/value lines with the keys padded to one width.</summary>
    public static void WriteFields(TextWriter output, params (string Key, string Value)[] fields)
    {
        var width = fields.Max(f => f.Key.Length);
        foreach (var (key, value) in fields)
        {
            output.WriteLine($"  {key.PadRight(width)}  {value}");
        }
    }

    /// <summary>Up to 10 decimals with trailing zeros removed (prices are stored as <c>numeric(28,10)</c>).</summary>
    public static string Price(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    public static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
