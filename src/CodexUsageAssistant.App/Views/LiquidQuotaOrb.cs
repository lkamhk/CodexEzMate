using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CodexUsageAssistant.Views;

// Shared frozen artwork and a visibility-bound 20 fps timer keep the tray resident lightweight.
public sealed class LiquidQuotaOrb : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(LiquidQuotaOrb),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LiquidQuotaOrb)d).UpdateAnimation()));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static readonly Brush Glass = Radial("#183F59", "#071929", "#020910", new Point(.30, .21));
    private static readonly Brush Liquid = Radial("#55F6FF", "#06BBDD", "#075270", new Point(.25, .05));
    private static readonly Brush RearLiquid = Linear("#403DD9EC", "#B0179DBA", "#FF073E61");
    private static readonly Brush Reflection = Linear("#DDDEFFFF", "#487DC9EA", "#007DC9EA");
    private static readonly Brush Refraction = FrozenBrush("#4079D7F0");
    private static readonly Brush Glint = FrozenBrush("#D8EEFFFF");
    private static readonly Brush Speck = FrozenBrush("#704EEDFF");
    private static readonly Brush Halo = Radial("#0031D6FF", "#0031D6FF", "#2431D6FF", new Point(.5, .5));
    private static readonly Brush Shade = EdgeShade();
    private static readonly Pen Rim = FrozenPen("#8056B8D8", .7);
    private static readonly Pen LightRim = FrozenPen("#A58DFFFF", .7);
    private static readonly Pen BottomRim = FrozenPen("#A081F5FF", 1.2);
    private static readonly Geometry Sphere = Shape("M 68.5,36 A 32.5,32.5 0 1 1 3.5,36 A 32.5,32.5 0 1 1 68.5,36");
    private static readonly Geometry LeftReflection = Shape("M 10,33 C 7,17 22,6 36,7 C 24,10 17,18 14,32 Z");
    private static readonly Geometry RightReflection = Shape("M 60,21 C 70,36 65,50 60,56 C 64,42 63,31 60,21 Z");
    private static readonly Geometry BottomReflection = Shape("M 19,62 C 28,68 44,69 54,61");
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private double _phase;
    internal bool IsAnimating => _timer.IsEnabled;
    public LiquidQuotaOrb()
    {
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }
    private void UpdateAnimation()
    {
        if (_timer is null) return;
        if (IsLoaded && IsVisible && double.IsFinite(Value) && Value > 0 && Value < 100) _timer.Start();
        else _timer.Stop();
    }
    private void OnTick(object? sender, EventArgs e)
    {
        _phase = (_phase + .075) % (Math.PI * 2); InvalidateVisual();
    }
    private static Geometry Wave(double level, double amplitude, double phase, bool fill)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            double Y(double x) => level + amplitude * Math.Sin(x / 14 + phase) + amplitude * .28 * Math.Sin(x / 7 - phase);
            path.BeginFigure(new Point(1, Y(1)), fill, fill);
            for (var x = 3; x <= 73; x += 2) path.LineTo(new Point(x, Y(x)), true, false);
            if (fill) { path.LineTo(new Point(73, 73), true, false); path.LineTo(new Point(1, 73), true, false); }
        }
        geometry.Freeze(); return geometry;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var scale = Math.Min(ActualWidth, ActualHeight) / 72;
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 72 * scale) / 2, (ActualHeight - 72 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawEllipse(Halo, null, new Point(36, 36), 35.5, 35.5);
        var sphere = Sphere;
        dc.DrawGeometry(Glass, Rim, sphere);
        dc.PushClip(sphere);
        if (double.IsFinite(Value) && Value > 0)
        {
            var level = 68.5 - 65 * Math.Clamp(Value, 0, 100) / 100;
            var amplitude = Math.Min(1.8, Math.Min(level - 3.5, 68.5 - level) * .28);
            dc.DrawGeometry(RearLiquid, null, Wave(level - .7, amplitude * .75, -_phase + 1.7, true));
            var body = Wave(level + .5, amplitude, _phase, true);
            dc.DrawGeometry(Liquid, null, body);
            dc.PushClip(body);
            // Stable particles add depth without independent particle simulation.
            for (var i = 0; i < 34; i++)
            {
                var x = 8 + (i * 29 % 57); var y = 8 + (i * 17 % 57);
                dc.DrawEllipse(Speck, null, new Point(x, y), i % 4 == 0 ? .65 : .3, i % 4 == 0 ? .65 : .3);
            }
            dc.Pop();
            dc.DrawGeometry(null, LightRim, Wave(level + .5, amplitude, _phase, false));
        }
        dc.DrawGeometry(Shade, null, sphere);
        dc.DrawGeometry(Reflection, null, LeftReflection);
        dc.DrawGeometry(Refraction, null, RightReflection);
        dc.DrawGeometry(null, BottomRim, BottomReflection);
        dc.PushTransform(new RotateTransform(-38, 20, 15));
        dc.DrawEllipse(Glint, null, new Point(20, 15), 5.5, 1.6);
        dc.Pop();
        dc.Pop();
        dc.DrawGeometry(null, Rim, sphere);
        dc.Pop(); dc.Pop();
    }
    private static Geometry Shape(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }
    private static Brush EdgeShade()
    {
        var brush = new RadialGradientBrush { Center = new Point(.42, .35), GradientOrigin = new Point(.42, .35), RadiusX = .65, RadiusY = .68 };
        brush.GradientStops.Add(new(C("#00010B19"), 0));
        brush.GradientStops.Add(new(C("#00010B19"), .5));
        brush.GradientStops.Add(new(C("#60010B19"), .84));
        brush.GradientStops.Add(new(C("#DD010B19"), 1)); brush.Freeze(); return brush;
    }
    private static Brush Radial(string start, string middle, string end, Point origin)
    {
        var brush = new RadialGradientBrush { Center = new Point(.5, .5), GradientOrigin = origin, RadiusX = .65, RadiusY = .65 };
        brush.GradientStops.Add(new(C(start), 0)); brush.GradientStops.Add(new(C(middle), .55)); brush.GradientStops.Add(new(C(end), 1)); brush.Freeze(); return brush;
    }
    private static Brush Linear(string start, string middle, string end)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new(C(start), 0)); brush.GradientStops.Add(new(C(middle), .5)); brush.GradientStops.Add(new(C(end), 1)); brush.Freeze(); return brush;
    }
    private static Brush FrozenBrush(string color) { var brush = new SolidColorBrush(C(color)); brush.Freeze(); return brush; }
    private static Pen FrozenPen(string color, double width) { var pen = new Pen(FrozenBrush(color), width); pen.Freeze(); return pen; }
}
