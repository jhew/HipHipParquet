using System.Windows;
using System.Windows.Media;

namespace HipHipParquet.Views;

/// <summary>
/// Code-behind access to the semantic theme brushes defined in Themes/*.xaml.
/// Use this instead of hardcoded <see cref="Brushes"/> values so programmatically
/// created UI follows the active theme.
/// </summary>
public static class ThemeBrushes
{
    public static Brush Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;
        return Brushes.Transparent;
    }

    public static Brush Window => Get("Brush.Window");
    public static Brush Card => Get("Brush.Card");
    public static Brush Pane => Get("Brush.Pane");
    public static Brush Subtle => Get("Brush.Subtle");
    public static Brush RowAlt => Get("Brush.RowAlt");
    public static Brush Border => Get("Brush.Border");
    public static Brush BorderNeutral => Get("Brush.BorderNeutral");
    public static Brush Overlay => Get("Brush.Overlay");
    public static Brush TextPrimary => Get("Brush.TextPrimary");
    public static Brush TextSecondary => Get("Brush.TextSecondary");
    public static Brush TextMuted => Get("Brush.TextMuted");
    public static Brush OnAccent => Get("Brush.OnAccent");
    public static Brush HeadingText => Get("Brush.HeadingText");
    public static Brush HeadingStrong => Get("Brush.HeadingStrong");
    public static Brush Accent => Get("Brush.Accent");
    public static Brush AccentHover => Get("Brush.AccentHover");
    public static Brush AccentText => Get("Brush.AccentText");
    public static Brush Success => Get("Brush.Success");
    public static Brush SuccessText => Get("Brush.SuccessText");
    public static Brush SuccessBg => Get("Brush.SuccessBg");
    public static Brush SuccessBorder => Get("Brush.SuccessBorder");
    public static Brush Danger => Get("Brush.Danger");
    public static Brush DangerText => Get("Brush.DangerText");
    public static Brush DangerBg => Get("Brush.DangerBg");
    public static Brush DangerBorder => Get("Brush.DangerBorder");
    public static Brush Warning => Get("Brush.Warning");
    public static Brush WarningText => Get("Brush.WarningText");
    public static Brush WarningDeep => Get("Brush.WarningDeep");
    public static Brush WarningBg => Get("Brush.WarningBg");
    public static Brush WarningBorder => Get("Brush.WarningBorder");
    public static Brush Info => Get("Brush.Info");
    public static Brush InfoText => Get("Brush.InfoText");
    public static Brush InfoBg => Get("Brush.InfoBg");
    public static Brush Special => Get("Brush.Special");
    public static Brush SpecialText => Get("Brush.SpecialText");
    public static Brush Earth => Get("Brush.Earth");
}
