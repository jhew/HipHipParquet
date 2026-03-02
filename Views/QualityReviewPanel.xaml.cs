using System.Windows;
using System.Windows.Controls;
using HipHipParquet.ViewModels;

namespace HipHipParquet.Views;

/// <summary>
/// Code-behind for the Quality Review Panel. Minimal — logic lives in QualityReviewViewModel.
/// Converters (InverseBooleanToVisibilityConverter, StringToBrushConverter) live in
/// HipHipParquet.Converters.BrushConverters and are referenced via xmlns:conv in XAML.
/// </summary>
public partial class QualityReviewPanel : UserControl
{
    public QualityReviewPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets the ViewModel as the DataContext. Called from MainWindow.
    /// </summary>
    public void SetViewModel(QualityReviewViewModel viewModel)
    {
        DataContext = viewModel;
    }
}
