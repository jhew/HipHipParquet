using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace HipHipParquet.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>
/// Applies and persists the application theme. Pairs WPF's built-in Fluent
/// ThemeMode (standard control styling, title bar) with the app's semantic
/// brush dictionaries in Themes/, and follows the OS setting when the
/// preference is <see cref="AppThemePreference.System"/>.
/// </summary>
public class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly string _preferencePath;
    private ResourceDictionary? _activeThemeDictionary;

    public AppThemePreference Preference { get; private set; } = AppThemePreference.System;

    /// <summary>Raised after the effective theme changes, with true when dark.</summary>
    public event EventHandler<bool>? EffectiveThemeChanged;

    public bool IsDarkEffective { get; private set; }

    public ThemeService(string? storageRoot = null)
    {
        var root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HipHipParquet");
        _preferencePath = Path.Combine(root, "theme-preference.json");
    }

    public void Initialize()
    {
        Preference = LoadPreference();
        Apply(Preference, persist: false);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Apply(AppThemePreference preference, bool persist = true)
    {
        Preference = preference;
        var dark = preference switch
        {
            AppThemePreference.Dark => true,
            AppThemePreference.Light => false,
            _ => IsSystemDark()
        };

        var app = Application.Current;
        if (app != null)
        {
#pragma warning disable WPF0001 // ThemeMode is experimental but shipped in .NET 9+
            app.ThemeMode = preference switch
            {
                AppThemePreference.Dark => ThemeMode.Dark,
                AppThemePreference.Light => ThemeMode.Light,
                _ => ThemeMode.System
            };
#pragma warning restore WPF0001

            var uri = new Uri(dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml", UriKind.Relative);
            var dictionary = new ResourceDictionary { Source = uri };
            if (_activeThemeDictionary != null)
                app.Resources.MergedDictionaries.Remove(_activeThemeDictionary);
            app.Resources.MergedDictionaries.Add(dictionary);
            _activeThemeDictionary = dictionary;
        }

        var changed = IsDarkEffective != dark;
        IsDarkEffective = dark;
        if (persist)
            SavePreference();
        if (changed)
            EffectiveThemeChanged?.Invoke(this, dark);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
            return;
        if (Preference != AppThemePreference.System)
            return;

        var app = Application.Current;
        // The event can arrive on a worker thread; theme swaps touch UI resources.
        app?.Dispatcher.BeginInvoke(() => Apply(AppThemePreference.System, persist: false));
    }

    private AppThemePreference LoadPreference()
    {
        try
        {
            if (!File.Exists(_preferencePath))
                return AppThemePreference.System;
            var json = File.ReadAllText(_preferencePath);
            var stored = JsonSerializer.Deserialize<StoredPreference>(json);
            return Enum.TryParse<AppThemePreference>(stored?.Theme, ignoreCase: true, out var parsed)
                ? parsed
                : AppThemePreference.System;
        }
        catch
        {
            return AppThemePreference.System;
        }
    }

    private void SavePreference()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_preferencePath)!);
            File.WriteAllText(_preferencePath, JsonSerializer.Serialize(new StoredPreference { Theme = Preference.ToString() }));
        }
        catch
        {
            // Theme preference persistence is best-effort.
        }
    }

    private sealed class StoredPreference
    {
        public string? Theme { get; set; }
    }
}
