using CommunityToolkit.Mvvm.ComponentModel;

namespace HipHipParquet.ViewModels;

/// <summary>
/// Header content for a data grid column.
/// </summary>
/// <remarks>
/// This is deliberately plain data rather than a visual. The grid runs with
/// <c>EnableColumnVirtualization</c>, so header containers are realised and recycled as the
/// user scrolls horizontally. A UIElement can only have one visual parent, so assigning a live
/// element to <see cref="System.Windows.Controls.DataGridColumn.Header"/> means the element is
/// re-parented on recycle and whichever presenter loses it renders blank — which is what caused
/// columns on wide files to lose their header text at random. Binding a header template against
/// this object instead lets WPF build a fresh visual for every realised header.
/// </remarks>
public partial class ColumnHeaderViewModel : ObservableObject
{
    public ColumnHeaderViewModel(string columnName, string typeIcon)
    {
        ColumnName = columnName;
        TypeIcon = typeIcon;
    }

    /// <summary>The underlying column name. Also the filter dictionary key and sort member path.</summary>
    public string ColumnName { get; }

    /// <summary>Emoji glyph for the column's data type.</summary>
    public string TypeIcon { get; }

    public string FilterToolTip => $"Filter {ColumnName}";

    /// <summary>
    /// Surfaces the column name to UI Automation. DataGridColumnHeader derives its
    /// accessible name from the header object, so without this screen readers announce the
    /// type name instead of the column.
    /// </summary>
    public override string ToString() => ColumnName;

    /// <summary>Drives the small dot shown when a column filter is applied.</summary>
    [ObservableProperty]
    private bool _isFilterActive;
}
