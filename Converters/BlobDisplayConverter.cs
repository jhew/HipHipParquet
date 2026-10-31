using System.Globalization;
using System.Windows.Data;
using HipHipParquet.Services;

namespace HipHipParquet.Converters;

/// <summary>
/// Renders BLOB (byte[]) cells as hex, e.g. 0x0000002A000001F80003, instead of "System.Byte[]".
/// </summary>
public class BlobDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => DuckDbValueHelper.ToDisplayString(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
