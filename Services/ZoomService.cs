using System.IO;
using System.Text.Json;

namespace HipHipParquet.Services;

/// <summary>
/// Holds and persists the zoom factor applied to the main content area.
/// Levels come from a fixed ladder so repeated Ctrl+wheel notches land on
/// predictable values instead of drifting through floating-point accumulation.
/// </summary>
public class ZoomService
{
    public const double DefaultZoom = 1.0;
    private const double Epsilon = 0.0001;

    private static readonly double[] Levels =
        [0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    private readonly string _preferencePath;

    public ZoomService(string? storageRoot = null)
    {
        var root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HipHipParquet");
        _preferencePath = Path.Combine(root, "zoom-preference.json");
    }

    /// <summary>The active zoom factor, where 1.0 is 100%.</summary>
    public double Current { get; private set; } = DefaultZoom;

    public double Minimum => Levels[0];
    public double Maximum => Levels[^1];

    public bool CanZoomIn => Current < Maximum - Epsilon;
    public bool CanZoomOut => Current > Minimum + Epsilon;
    public bool IsDefault => Math.Abs(Current - DefaultZoom) < Epsilon;

    /// <summary>Raised after the factor changes, with the new value.</summary>
    public event EventHandler<double>? ZoomChanged;

    /// <summary>
    /// Loads the persisted factor. Deliberately does not raise
    /// <see cref="ZoomChanged"/> so the caller can apply the initial value once.
    /// </summary>
    public void Initialize() => Current = LoadPreference();

    /// <summary>Steps up one level. Returns false when already at <see cref="Maximum"/>.</summary>
    public bool ZoomIn()
    {
        foreach (var level in Levels)
        {
            if (level > Current + Epsilon)
                return Set(level);
        }
        return false;
    }

    /// <summary>Steps down one level. Returns false when already at <see cref="Minimum"/>.</summary>
    public bool ZoomOut()
    {
        for (var i = Levels.Length - 1; i >= 0; i--)
        {
            if (Levels[i] < Current - Epsilon)
                return Set(Levels[i]);
        }
        return false;
    }

    public bool Reset() => Set(DefaultZoom);

    /// <summary>Sets an arbitrary factor, clamped to the supported range.</summary>
    public bool Set(double zoom, bool persist = true)
    {
        if (double.IsNaN(zoom) || double.IsInfinity(zoom))
            return false;

        var clamped = Math.Clamp(zoom, Minimum, Maximum);
        if (Math.Abs(clamped - Current) < Epsilon)
            return false;

        Current = clamped;
        if (persist)
            SavePreference();
        ZoomChanged?.Invoke(this, Current);
        return true;
    }

    private double LoadPreference()
    {
        try
        {
            if (!File.Exists(_preferencePath))
                return DefaultZoom;

            var stored = JsonSerializer.Deserialize<StoredPreference>(File.ReadAllText(_preferencePath));
            if (stored is null || double.IsNaN(stored.Zoom) || double.IsInfinity(stored.Zoom) || stored.Zoom <= 0)
                return DefaultZoom;

            return Math.Clamp(stored.Zoom, Minimum, Maximum);
        }
        catch
        {
            return DefaultZoom;
        }
    }

    private void SavePreference()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_preferencePath)!);
            File.WriteAllText(_preferencePath, JsonSerializer.Serialize(new StoredPreference { Zoom = Current }));
        }
        catch
        {
            // Zoom preference persistence is best-effort.
        }
    }

    private sealed class StoredPreference
    {
        public double Zoom { get; set; } = DefaultZoom;
    }
}
