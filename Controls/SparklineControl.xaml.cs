using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HipHipParquet.Models;

namespace HipHipParquet.Controls;

/// <summary>
/// A lightweight sparkline/histogram control. Renders HistogramBucket data when available;
/// falls back to ValueFrequency (top-values) bars otherwise.
/// </summary>
public partial class SparklineControl : UserControl
{
    public static readonly DependencyProperty BucketsProperty =
        DependencyProperty.Register(nameof(Buckets), typeof(List<HistogramBucket>),
            typeof(SparklineControl), new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty TopValuesProperty =
        DependencyProperty.Register(nameof(TopValues), typeof(List<ValueFrequency>),
            typeof(SparklineControl), new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty BarColorProperty =
        DependencyProperty.Register(nameof(BarColor), typeof(Brush),
            typeof(SparklineControl), new PropertyMetadata(Views.ThemeBrushes.Accent));

    public List<HistogramBucket>? Buckets
    {
        get => (List<HistogramBucket>?)GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    public List<ValueFrequency>? TopValues
    {
        get => (List<ValueFrequency>?)GetValue(TopValuesProperty);
        set => SetValue(TopValuesProperty, value);
    }

    public Brush BarColor
    {
        get => (Brush)GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    public SparklineControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SparklineControl control)
            control.Render();
    }

    private void Render()
    {
        SparklineCanvas.Children.Clear();

        var width = SparklineCanvas.ActualWidth;
        var height = SparklineCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var buckets = Buckets;
        if (buckets != null && buckets.Count > 0)
        {
            RenderHistogram(buckets, width, height);
            return;
        }

        var topValues = TopValues;
        if (topValues != null && topValues.Count > 0)
            RenderTopValues(topValues, width, height);
    }

    private void RenderHistogram(List<HistogramBucket> buckets, double width, double height)
    {
        var maxCount = buckets.Max(b => b.Count);
        if (maxCount == 0) return;

        var barWidth = Math.Max(2, (width - (buckets.Count - 1)) / buckets.Count);
        var gap = 1.0;

        for (int i = 0; i < buckets.Count; i++)
        {
            var barHeight = (double)buckets[i].Count / maxCount * height;
            var rect = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(1, barHeight),
                Fill = BarColor,
                RadiusX = 1,
                RadiusY = 1,
                ToolTip = $"{buckets[i].Label}: {buckets[i].Count:N0}"
            };
            Canvas.SetLeft(rect, i * (barWidth + gap));
            Canvas.SetTop(rect, height - barHeight);
            SparklineCanvas.Children.Add(rect);
        }
    }

    private void RenderTopValues(List<ValueFrequency> topValues, double width, double height)
    {
        var maxCount = topValues.Max(v => v.Count);
        if (maxCount == 0) return;

        var barWidth = Math.Max(2, (width - (topValues.Count - 1)) / topValues.Count);
        var gap = 1.0;

        for (int i = 0; i < topValues.Count; i++)
        {
            var barHeight = (double)topValues[i].Count / maxCount * height;
            var rect = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(1, barHeight),
                Fill = BarColor,
                RadiusX = 1,
                RadiusY = 1,
                ToolTip = $"{topValues[i].Value}: {topValues[i].Percentage:F1}%"
            };
            Canvas.SetLeft(rect, i * (barWidth + gap));
            Canvas.SetTop(rect, height - barHeight);
            SparklineCanvas.Children.Add(rect);
        }
    }
}
