using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HipHipParquet.Converters;

/// <summary>
/// Converts a boolean to Visibility, returning Collapsed when true and Visible when false.
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Collapsed;
    }
}

/// <summary>
/// Converts a hex color string (e.g. "#4CAF50") to a WPF SolidColorBrush.
/// Brushes are cached and frozen to avoid repeated allocations during binding.
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> _cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            if (_cache.TryGetValue(hex, out var cached))
                return cached;

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                _cache[hex] = brush;
                return brush;
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"[StringToBrushConverter] Invalid hex color: '{hex}'");
            }
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Given the current ColumnSortBy string and a column name as ConverterParameter,
/// returns " ↑", " ↓", or "" (note the leading space used to separate the indicator
/// from the header label) depending on whether that column is the active sort and its direction.
/// Usage: Text="{Binding ColumnSortBy, Converter={StaticResource SortIndicatorConverter}, ConverterParameter=Name}"
/// </summary>
public class SortIndicatorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string sortBy || parameter is not string columnName)
            return string.Empty;

        var active = sortBy.Replace(" ↑", "").Replace(" ↓", "").Trim();
        if (!string.Equals(active, columnName, StringComparison.Ordinal))
            return string.Empty;

        return sortBy.EndsWith("↑") ? " ↑" : " ↓";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
