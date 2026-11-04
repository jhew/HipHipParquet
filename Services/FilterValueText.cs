using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HipHipParquet.Services;

/// <summary>
/// Turns a column-filter key (DuckDB's <c>CAST(col AS VARCHAR)</c> text) into the text the grid
/// shows and the in-memory <see cref="DataView.RowFilter"/> compares via <c>Convert(col, 'System.String')</c>.
///
/// <code>
///   DuckDB VARCHAR                      .NET (en-US)
///   10.00000   (DECIMAL(38,5))          10
///   10.0       (DOUBLE)                 10
///   true       (BOOLEAN)                True
///   2024-01-05 (DATE)                   1/5/2024
///   2024-01-05 10:00:00 (TIMESTAMP)     1/5/2024 10:00:00 AM
/// </code>
///
/// Keys read by the distinct query carry their grid text from the loaded value
/// (<see cref="DuckDbValueHelper.ReadFilterValuesAsync"/>); this parser is the fallback for keys
/// restored from a saved view or workspace before that query has run. Keys it can't translate
/// are returned unchanged.
/// </summary>
public static class FilterValueText
{
    /// <summary>
    /// The text <c>Convert(col, 'System.String')</c> produces in a RowFilter on
    /// <paramref name="column"/>'s table for the value DuckDB rendered as <paramref name="duckDbText"/>.
    /// </summary>
    public static string ToDataViewText(string duckDbText, DataColumn? column)
    {
        if (column == null || string.IsNullOrEmpty(duckDbText) || column.DataType == typeof(string))
            return duckDbText;

        if (!TryParse(duckDbText, column.DataType, out var value))
            return duckDbText;

        // DuckDB.NET loads DECIMALs wider than 18 digits (HUGEINT-backed) without trailing zeros.
        if (value is decimal m && IsWideDecimal(column))
            value = m / 1.000000000000000000000000000000000m;

        return DuckDbValueHelper.ToFilterText(value, GetFormatProvider(column.Table)) ?? duckDbText;
    }

    /// <summary>
    /// The provider a DataView expression uses for <c>Convert</c>: the table's Locale, or the
    /// invariant culture when that Locale is neutral (mirrors DataTable.FormatProvider).
    /// </summary>
    public static IFormatProvider GetFormatProvider(DataTable? table)
    {
        var culture = table?.Locale ?? CultureInfo.CurrentCulture;
        return culture.IsNeutralCulture ? CultureInfo.InvariantCulture : culture;
    }

    /// <summary>Parses DuckDB's VARCHAR rendering of a value into <paramref name="type"/>.</summary>
    public static bool TryParse(string text, Type type, out object value)
    {
        var inv = CultureInfo.InvariantCulture;
        const NumberStyles Integer = NumberStyles.Integer;
        value = null!;
        bool ok;

        if (type == typeof(DateOnly))
        {
            ok = DateOnly.TryParseExact(text, "yyyy-MM-dd", inv, DateTimeStyles.None, out var date); value = date; return ok;
        }
        if (type == typeof(TimeOnly))
        {
            ok = TimeOnly.TryParse(text, inv, DateTimeStyles.None, out var time); value = time; return ok;
        }

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean:
                ok = bool.TryParse(text, out var b); value = b; return ok;
            case TypeCode.SByte:
                ok = sbyte.TryParse(text, Integer, inv, out var i8); value = i8; return ok;
            case TypeCode.Byte:
                ok = byte.TryParse(text, Integer, inv, out var u8); value = u8; return ok;
            case TypeCode.Int16:
                ok = short.TryParse(text, Integer, inv, out var i16); value = i16; return ok;
            case TypeCode.UInt16:
                ok = ushort.TryParse(text, Integer, inv, out var u16); value = u16; return ok;
            case TypeCode.Int32:
                ok = int.TryParse(text, Integer, inv, out var i32); value = i32; return ok;
            case TypeCode.UInt32:
                ok = uint.TryParse(text, Integer, inv, out var u32); value = u32; return ok;
            case TypeCode.Int64:
                ok = long.TryParse(text, Integer, inv, out var i64); value = i64; return ok;
            case TypeCode.UInt64:
                ok = ulong.TryParse(text, Integer, inv, out var u64); value = u64; return ok;
            case TypeCode.Single:
                ok = TryParseReal(text, out var f); value = (float)f; return ok;
            case TypeCode.Double:
                ok = TryParseReal(text, out var d); value = d; return ok;
            case TypeCode.Decimal:
                ok = decimal.TryParse(text, NumberStyles.Float, inv, out var m); value = m; return ok;
            case TypeCode.DateTime:
                // DuckDB writes TIMESTAMP as yyyy-MM-dd HH:mm:ss[.ffffff]. TIMESTAMPTZ carries an
                // offset whose meaning depends on how the reader materialized the cell, so it is
                // left untranslated rather than guessed.
                if (HasUtcOffset(text)) return false;
                ok = DateTime.TryParse(text, inv, DateTimeStyles.None, out var dt); value = dt; return ok;
            default:
                // Guid, BigInteger, TimeSpan, nested types: left as DuckDB's text.
                return false;
        }
    }

    private static bool IsWideDecimal(DataColumn column)
    {
        if (column.ExtendedProperties[DuckDbValueHelper.DuckDbTypeProperty] is not string duckDbType)
            return false;

        var match = Regex.Match(duckDbType, @"^DECIMAL\((\d+),\s*\d+\)$", RegexOptions.IgnoreCase);
        return match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) > 18;
    }

    private static bool TryParseReal(string text, out double value)
    {
        switch (text.ToLowerInvariant())
        {
            case "inf": case "+inf": case "infinity": value = double.PositiveInfinity; return true;
            case "-inf": case "-infinity": value = double.NegativeInfinity; return true;
            case "nan": case "-nan": value = double.NaN; return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool HasUtcOffset(string text)
    {
        // Offset follows the time part: "...HH:mm:ss+00", "...-05:30". A date's own dashes
        // sit before the space/T separator, so only look after it.
        var sep = text.IndexOfAny([' ', 'T']);
        if (sep < 0) return false;
        var timePart = text.AsSpan(sep + 1);
        return timePart.IndexOfAny('+', '-') >= 0 || timePart.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
    }
}
