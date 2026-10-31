using System.Data;
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
    /// <see cref="DataColumn.ExtendedProperties"/> key holding the column's DuckDB SQL type as
    /// loaded, e.g. <c>DECIMAL(18,4)</c> or <c>TIMESTAMP WITH TIME ZONE</c>. Save recreates the
    /// column with this type; the .NET type alone loses precision, scale and time zone.
    /// </summary>
    public const string DuckDbTypeProperty = "DuckDbType";

    /// <summary>
    /// Returns the DuckDB SQL type of each column <paramref name="selectSql"/> produces, or null
    /// if it can't be described. <c>DuckDBDataReader.GetDataTypeName</c> is no substitute: it
    /// reports the type family ("Decimal"), not the declared type ("DECIMAL(18,4)").
    /// </summary>
    public static async Task<IReadOnlyList<string>?> DescribeColumnTypesAsync(
        DbConnection connection, string selectSql, CancellationToken cancellationToken = default)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DESCRIBE {selectSql}";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var types = new List<string>();
            var typeOrdinal = reader.GetOrdinal("column_type");
            while (await reader.ReadAsync(cancellationToken))
                types.Add(reader.GetString(typeOrdinal));
            return types;
        }
        catch (DbException)
        {
            // Types are a save-time refinement; loading must not fail over them.
            return null;
        }
    }

    /// <summary>
    /// Adds a column per reader field, typed for storage via <see cref="ResolveColumnType"/> and
    /// tagged with its DuckDB type when <paramref name="duckDbTypes"/> matches the reader.
    /// </summary>
    public static void AddColumns(DataTable table, DbDataReader reader, IReadOnlyList<string>? duckDbTypes)
    {
        var tagTypes = duckDbTypes != null && duckDbTypes.Count == reader.FieldCount;
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var column = table.Columns.Add(reader.GetName(i), ResolveColumnType(reader.GetFieldType(i)));
            if (tagTypes)
                column.ExtendedProperties[DuckDbTypeProperty] = duckDbTypes![i];
        }
    }

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
    /// True for STRUCT and MAP values (read as dictionaries) and lists of them. DuckDB.NET can't
    /// bind these as parameters, so they are sent as JSON and cast back to the column type.
    /// </summary>
    public static bool NeedsJsonBinding(Type type)
        => typeof(System.Collections.IDictionary).IsAssignableFrom(type)
           || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
               && NeedsJsonBinding(type.GetGenericArguments()[0]));

    /// <summary>JSON text of a nested value, for columns where <see cref="NeedsJsonBinding"/> holds.</summary>
    public static object ToJsonParameterValue(object? value) => value is null or DBNull
        ? DBNull.Value
        : System.Text.Json.JsonSerializer.Serialize(value, NestedJsonOptions);

    private static readonly System.Text.Json.JsonSerializerOptions NestedJsonOptions = new()
    {
        // HUGEINT fields inside a STRUCT read as BigInteger, which System.Text.Json can't write.
        Converters = { new BigIntegerJsonConverter() }
    };

    private sealed class BigIntegerJsonConverter : System.Text.Json.Serialization.JsonConverter<System.Numerics.BigInteger>
    {
        public override System.Numerics.BigInteger Read(
            ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(
            System.Text.Json.Utf8JsonWriter writer, System.Numerics.BigInteger value, System.Text.Json.JsonSerializerOptions options)
            => writer.WriteRawValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

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
