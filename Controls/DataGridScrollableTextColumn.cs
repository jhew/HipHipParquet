using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace HipHipParquet.Controls;

/// <summary>
/// A bound text column whose cells host a <see cref="ScrollViewer"/>, so an over-long value can
/// be scrolled within its cell instead of being clipped.
/// </summary>
/// <remarks>
/// This replaces a pair of per-column <see cref="DataTemplate"/>s that were assembled with
/// <c>FrameworkElementFactory</c>. That approach built two templates and a style for every
/// column — on a 220-column file, over 600 objects — where WPF expects a single shared
/// definition, and <c>FrameworkElementFactory</c> is a legacy API that Microsoft no longer
/// recommends. Generating the cell element directly is the supported extension point: the grid
/// creates and recycles elements through it, so virtualisation behaves normally and there is one
/// code path for every column.
/// </remarks>
public class DataGridScrollableTextColumn : DataGridBoundColumn
{
    /// <summary>Right-aligns the cell content. Set for numeric columns.</summary>
    public bool IsNumeric { get; init; }

    /// <summary>
    /// Binding used while editing. Kept separate from <see cref="DataGridBoundColumn.Binding"/>
    /// so the editor can commit on lost focus while the display binding stays read-optimised.
    /// </summary>
    public BindingBase? EditingBinding { get; init; }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (Binding != null)
            BindingOperations.SetBinding(text, TextBlock.TextProperty, Binding);

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Cells own focus and keyboard navigation; the scroller must not take it.
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = IsNumeric ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Content = text
        };
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var editor = new TextBox
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            TextAlignment = IsNumeric ? TextAlignment.Right : TextAlignment.Left
        };

        var binding = EditingBinding ?? Binding;
        if (binding != null)
            BindingOperations.SetBinding(editor, TextBox.TextProperty, binding);

        return editor;
    }
}
