using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HipHipParquet.Models;

namespace HipHipParquet.Converters;

/// <summary>
/// Converts a string to Visibility: Visible when non-null/non-empty, Collapsed otherwise.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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

    /// <summary>
    /// Well-known status hexes produced by view models and models (QualityScore,
    /// NarrativeItem) mapped to semantic theme brushes so bound status colors
    /// follow the active light/dark theme.
    /// </summary>
    private static readonly Dictionary<string, string> _semanticMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#F44336"] = "Brush.Danger",
        ["#FF9800"] = "Brush.Warning",
        ["#4CAF50"] = "Brush.Success",
        ["#9E9E9E"] = "Brush.TextMuted",
        ["#E8F5E9"] = "Brush.SuccessBg",
        ["#FFF3E0"] = "Brush.WarningBg",
        ["#FFEBEE"] = "Brush.DangerBg",
        ["#2E7D32"] = "Brush.SuccessText",
        ["#E65100"] = "Brush.WarningText",
        ["#C62828"] = "Brush.DangerText",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            if (_semanticMap.TryGetValue(hex, out var key) &&
                Application.Current?.TryFindResource(key) is Brush themed)
                return themed;

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
        return Views.ThemeBrushes.TextMuted;
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

/// <summary>Shows a <see cref="MarkdownFlavorProfile"/> by its friendly name in pickers.</summary>
public class MarkdownProfileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MarkdownFlavorProfile profile ? profile.GetDisplayName() : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
