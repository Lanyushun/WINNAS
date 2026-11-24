using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Winnas.Console.Controls;

/// <summary>
/// A minimal two-series rolling line chart.
///
/// Hand-drawn on purpose: the whole app avoids third-party packages, and a
/// 60-point sparkline does not justify pulling in a charting library. Series
/// arrive as plain lists; the caller owns smoothing and retention.
/// </summary>
public class RateChart : Control
{
    static RateChart() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(RateChart),
            new FrameworkPropertyMetadata(typeof(RateChart)));

    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(
        nameof(Primary), typeof(IList<double>), typeof(RateChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        nameof(Secondary), typeof(IList<double>), typeof(RateChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Value that maps to the top of the chart. Zero means auto from data.</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale), typeof(double), typeof(RateChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PrimaryBrushProperty = DependencyProperty.Register(
        nameof(PrimaryBrush), typeof(Brush), typeof(RateChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryBrushProperty = DependencyProperty.Register(
        nameof(SecondaryBrush), typeof(Brush), typeof(RateChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillAreaProperty = DependencyProperty.Register(
        nameof(FillArea), typeof(bool), typeof(RateChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<double>? Primary
    {
        get => (IList<double>?)GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public IList<double>? Secondary
    {
        get => (IList<double>?)GetValue(SecondaryProperty);
        set => SetValue(SecondaryProperty, value);
    }

    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public Brush PrimaryBrush
    {
        get => (Brush)GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public Brush SecondaryBrush
    {
        get => (Brush)GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public bool FillArea
    {
        get => (bool)GetValue(FillAreaProperty);
        set => SetValue(FillAreaProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4) return;

        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF3)), 1);
        for (int i = 1; i <= 3; i++)
        {
            var y = h * i / 4.0;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        var scale = Scale > 0 ? Scale : AutoScale();
        if (scale <= 0) scale = 1;

        DrawSeries(dc, Secondary, SecondaryBrush, w, h, scale, FillArea);
        DrawSeries(dc, Primary, PrimaryBrush, w, h, scale, FillArea);
    }

    private double AutoScale()
    {
        double max = 0;
        foreach (var v in Primary ?? Array.Empty<double>()) if (v > max) max = v;
        foreach (var v in Secondary ?? Array.Empty<double>()) if (v > max) max = v;

        // A chart pinned exactly to its own peak jumps constantly; headroom and
        // a floor keep a quiet link from looking busy.
        return Math.Max(max * 1.25, 1024 * 256);
    }

    private static void DrawSeries(DrawingContext dc, IList<double>? data, Brush stroke,
        double w, double h, double scale, bool fill)
    {
        if (data is null || data.Count < 2) return;

        var step = w / (data.Count - 1);
        var points = new Point[data.Count];
        for (int i = 0; i < data.Count; i++)
        {
            var ratio = Math.Clamp(data[i] / scale, 0, 1);
            points[i] = new Point(i * step, h - ratio * h);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, true);
        }
        geometry.Freeze();

        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                foreach (var p in points) ctx.LineTo(p, true, true);
                ctx.LineTo(new Point(points[^1].X, h), true, true);
            }
            area.Freeze();

            var fillBrush = stroke.Clone();
            fillBrush.Opacity = 0.14;
            dc.DrawGeometry(fillBrush, null, area);
        }

        dc.DrawGeometry(null, new Pen(stroke, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            geometry);
    }
}
