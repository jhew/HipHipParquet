using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace HipHipParquet.Views;

public partial class HelpDialog : Window
{
    public HelpDialog(int initialTab = 0)
    {
        InitializeComponent();
        HelpTabControl.SelectedIndex = initialTab;

        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = v != null
            ? $"Version {v.Major}.{v.Minor}.{v.Build}"
            : "Version 1.0.0";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
