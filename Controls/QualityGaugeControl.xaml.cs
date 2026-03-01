using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HipHipParquet.Controls;

/// <summary>
/// A semicircular gauge control that visualizes a 0–100 quality score with color coding.
/// </summary>
public partial class QualityGaugeControl : UserControl
{
    public static readonly DependencyProperty ScoreProperty =
        DependencyProperty.Register(nameof(Score), typeof(double),
            typeof(QualityGaugeControl), new PropertyMetadata(0.0, OnScoreChanged));

    public static readonly DependencyProperty GradeProperty =
        DependencyProperty.Register(nameof(Grade), typeof(string),
            typeof(QualityGaugeControl), new PropertyMetadata("", OnGradeChanged));

    public double Score
    {
        get => (double)GetValue(ScoreProperty);
        set => SetValue(ScoreProperty, value);
    }

    public string Grade
    {
        get => (string)GetValue(GradeProperty);
        set => SetValue(GradeProperty, value);
    }

    public QualityGaugeControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    private static void OnScoreChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QualityGaugeControl gauge)
            gauge.Render();
    }

    private static void OnGradeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QualityGaugeControl gauge)
            gauge.GradeText.Text = gauge.Grade;
    }

    private void Render()
    {
        GaugeCanvas.Children.Clear();

        var centerX = 70.0;
        var centerY = 75.0;
        var radius = 60.0;
        var thickness = 10.0;

        // Background arc (full semicircle)
        DrawArc(centerX, centerY, radius, thickness, 180, 360, Brushes.LightGray);

        // Score arc
        var scoreAngle = 180 + (Score / 100.0) * 180;
        var scoreColor = GetScoreColor(Score);
        DrawArc(centerX, centerY, radius, thickness, 180, scoreAngle, scoreColor);

        // Score text
        ScoreText.Text = Score.ToString("F0");
        ScoreText.Foreground = scoreColor;
        GradeText.Text = Grade;
    }

    private void DrawArc(double cx, double cy, double r, double thickness, double startAngle, double endAngle, Brush color)
    {
        var outerR = r;
        var innerR = r - thickness;

        var startRad = startAngle * Math.PI / 180;
        var endRad = endAngle * Math.PI / 180;

        var outerStartX = cx + outerR * Math.Cos(startRad);
        var outerStartY = cy + outerR * Math.Sin(startRad);
        var outerEndX = cx + outerR * Math.Cos(endRad);
        var outerEndY = cy + outerR * Math.Sin(endRad);

        var innerStartX = cx + innerR * Math.Cos(endRad);
        var innerStartY = cy + innerR * Math.Sin(endRad);
        var innerEndX = cx + innerR * Math.Cos(startRad);
        var innerEndY = cy + innerR * Math.Sin(startRad);

        var isLargeArc = (endAngle - startAngle) > 180;

        var figure = new PathFigure
        {
            StartPoint = new Point(outerStartX, outerStartY),
            IsClosed = true,
            IsFilled = true
        };

        // Outer arc
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(outerEndX, outerEndY),
            Size = new Size(outerR, outerR),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise
        });

        // Line to inner arc
        figure.Segments.Add(new LineSegment(new Point(innerStartX, innerStartY), true));

        // Inner arc (reverse direction)
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(innerEndX, innerEndY),
            Size = new Size(innerR, innerR),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Counterclockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var path = new Path
        {
            Data = geometry,
            Fill = color
        };

        GaugeCanvas.Children.Add(path);
    }

    private static SolidColorBrush GetScoreColor(double score) => score switch
    {
        >= 80 => new SolidColorBrush(Color.FromRgb(76, 175, 80)),   // Green
        >= 60 => new SolidColorBrush(Color.FromRgb(255, 152, 0)),   // Orange
        _ => new SolidColorBrush(Color.FromRgb(244, 67, 54))        // Red
    };
}
