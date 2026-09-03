using System.IO;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class ZoomServiceTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), $"hiphipparquet-zoom-{Guid.NewGuid():N}");

    private static void WithRoot(Action<string> body)
    {
        var root = NewRoot();
        try
        {
            body(root);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Initialize_WithNoStoredPreference_UsesDefault() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Initialize();

        Assert.Equal(ZoomService.DefaultZoom, service.Current);
        Assert.True(service.IsDefault);
    });

    [Fact]
    public void ZoomIn_And_ZoomOut_StepThroughTheLadder() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Initialize();

        Assert.True(service.ZoomIn());
        Assert.Equal(1.1, service.Current, 3);

        Assert.True(service.ZoomIn());
        Assert.Equal(1.25, service.Current, 3);

        Assert.True(service.ZoomOut());
        Assert.Equal(1.1, service.Current, 3);

        Assert.True(service.ZoomOut());
        Assert.Equal(1.0, service.Current, 3);
    });

    [Fact]
    public void ZoomIn_FromAnArbitraryFactor_SnapsToTheNextLadderStep() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Set(1.3);

        Assert.True(service.ZoomIn());
        Assert.Equal(1.5, service.Current, 3);
    });

    [Fact]
    public void ZoomIn_AtMaximum_DoesNothing() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Set(service.Maximum);

        Assert.False(service.ZoomIn());
        Assert.Equal(service.Maximum, service.Current, 3);
        Assert.False(service.CanZoomIn);
    });

    [Fact]
    public void ZoomOut_AtMinimum_DoesNothing() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Set(service.Minimum);

        Assert.False(service.ZoomOut());
        Assert.Equal(service.Minimum, service.Current, 3);
        Assert.False(service.CanZoomOut);
    });

    [Fact]
    public void Set_ClampsOutOfRangeValues() => WithRoot(root =>
    {
        var service = new ZoomService(root);

        service.Set(99);
        Assert.Equal(service.Maximum, service.Current, 3);

        service.Set(0.01);
        Assert.Equal(service.Minimum, service.Current, 3);
    });

    [Fact]
    public void Set_RejectsNonFiniteValues() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Initialize();

        Assert.False(service.Set(double.NaN));
        Assert.False(service.Set(double.PositiveInfinity));
        Assert.Equal(ZoomService.DefaultZoom, service.Current);
    });

    [Fact]
    public void Set_ToTheCurrentValue_IsANoOpAndDoesNotRaise() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Set(1.5);

        var raised = 0;
        service.ZoomChanged += (_, _) => raised++;

        Assert.False(service.Set(1.5));
        Assert.Equal(0, raised);
    });

    [Fact]
    public void ZoomChanged_ReportsTheNewFactor() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Initialize();

        double? reported = null;
        service.ZoomChanged += (_, zoom) => reported = zoom;

        service.ZoomIn();

        Assert.Equal(service.Current, reported);
    });

    [Fact]
    public void Reset_ReturnsToDefault() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Set(2.0);

        Assert.True(service.Reset());
        Assert.True(service.IsDefault);
    });

    [Fact]
    public void Set_PersistsAcrossInstances() => WithRoot(root =>
    {
        var service = new ZoomService(root);
        service.Set(1.75);

        var reloaded = new ZoomService(root);
        reloaded.Initialize();

        Assert.Equal(1.75, reloaded.Current, 3);
    });

    [Fact]
    public void Initialize_WithCorruptPreferenceFile_FallsBackToDefault() => WithRoot(root =>
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "zoom-preference.json"), "{ this is not json");

        var service = new ZoomService(root);
        service.Initialize();

        Assert.Equal(ZoomService.DefaultZoom, service.Current);
    });

    [Fact]
    public void Initialize_WithOutOfRangeStoredValue_ClampsIt() => WithRoot(root =>
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "zoom-preference.json"), "{\"Zoom\":42}");

        var service = new ZoomService(root);
        service.Initialize();

        Assert.Equal(service.Maximum, service.Current, 3);
    });
}
