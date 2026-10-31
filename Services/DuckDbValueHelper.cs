using System.Data.Common;
using System.IO;

namespace HipHipParquet.Services;

/// <summary>
/// Normalizes values crossing the DuckDB.NET boundary.
///
/// DuckDB.NET returns BLOB values from <c>GetValue</c> as an <see cref="UnmanagedMemoryStream"/>
/// that points into DuckDB's own vector memory. That memory is only valid while the reader is
/// positioned on the row, so BLOBs must be copied into a <see cref="byte"/>[] at read time.
/// Storing the stream in a DataTable leaves a dangling pointer, displays as
/// "System.IO.UnmanagedMemoryStream", and fails on save because DuckDB.NET cannot bind a
/// <see cref="Stream"/> as a parameter.
/// </summary>
public static class DuckDbValueHelper
{
    /// <summary>Maximum bytes rendered when a BLOB is shown as hex text.</summary>
    public const int MaxDisplayBytes = 64;

    /// <summary>
    /// The DataTable column type to use for a reader field. BLOB fields report
    /// <see cref="Stream"/>; they are stored as <see cref="byte"/>[].
    /// </summary>
    public static Type ResolveColumnType(Type fieldType)
        => typeof(Stream).IsAssignableFrom(fieldType) ? typeof(byte[]) : fieldType;

    /// <summary>
    /// Reads one field from the current row, copying BLOB streams into a byte array
    /// while the reader is still positioned on the row.
    /// </summary>
    public static object MaterializeValue(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return DBNull.Value;

        return MaterializeValue(reader.GetValue(ordinal));
    }

    /// <summary>Copies a stream value into a byte array; other values pass through.</summary>
    public static object MaterializeValue(object? value)
    {
        if (value is null)
            return DBNull.Value;

        if (value is Stream stream)
        {
            using (stream)
                return ReadAllBytes(stream);
        }

        return value;
    }

    /// <summary>
    /// Converts a DataTable cell into something DuckDB.NET can bind as a parameter.
    /// A <see cref="Stream"/> is a defensive case only: it should never reach here once
    /// reads go through <see cref="MaterializeValue(DbDataReader, int)"/>.
    /// </summary>
    public static object ToParameterValue(object? value) => value switch
    {
        null or DBNull => DBNull.Value,
        Stream stream => ReadAllBytes(stream),
        _ => value
    };

    /// <summary>
    /// Text form of a value for display, copy, preview and composite keys.
    /// BLOBs render as hex (0x…) instead of "System.Byte[]".
    /// </summary>
    public static string ToDisplayString(object? value, int maxBytes = MaxDisplayBytes) => value switch
    {
        null or DBNull => string.Empty,
        byte[] bytes => FormatHex(bytes, maxBytes),
        Stream => "(binary)",
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Full, untruncated text form, safe for equality comparison of BLOBs.</summary>
    public static string ToComparisonString(object? value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => ToDisplayString(value)
    };

    public static string FormatHex(byte[] bytes, int maxBytes = MaxDisplayBytes)
    {
        if (bytes.Length == 0)
            return "0x";

        if (maxBytes <= 0 || bytes.Length <= maxBytes)
            return "0x" + Convert.ToHexString(bytes);

        return "0x" + Convert.ToHexString(bytes, 0, maxBytes) + $"… ({bytes.Length:N0} bytes)";
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream memoryStream)
            return memoryStream.ToArray();

        if (stream.CanSeek)
        {
            stream.Position = 0;
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
