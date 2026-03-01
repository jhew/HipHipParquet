using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HipHipParquet.ViewModels;

namespace HipHipParquet.Views;

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
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { }
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Code-behind for the QA Review Panel. Minimal — logic lives in QaReviewViewModel.
/// </summary>
public partial class QaReviewPanel : UserControl
{
    public QaReviewPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets the ViewModel as the DataContext. Called from MainWindow.
    /// </summary>
    public void SetViewModel(QaReviewViewModel viewModel)
    {
        DataContext = viewModel;
    }
}
