using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Xml;
using System.Xml.Linq;

namespace PCWatt.App.Controls;

/// <summary>Small renderer for our own bundled SVG shapes and SMIL opacity/dash/rotation tracks.
/// No browser, script engine, external URLs, rendering event loop or hardware access.</summary>
internal sealed class SvgMotionScene
{
    private sealed record Track(Animatable Target, DependencyProperty Property, AnimationTimeline Animation);
    private readonly List<Track> _tracks = [];
    public DrawingGroup Drawing { get; } = new();
    public bool IsRunning { get; private set; }
    internal int ActiveTrackCount => _tracks.Count(t => t.Target.HasAnimatedProperties);
    internal int TrackCount => _tracks.Count;

    public static SvgMotionScene Load(string asset)
    {
        if (asset is not ("money" or "whole" or "cpu" or "gpu" or "memory" or "other")) asset = "whole";
        using var stream = typeof(SvgMotionScene).Assembly.GetManifestResourceStream($"PCWatt.App.Assets.Motion.{asset}.svg")
            ?? throw new InvalidDataException("缺少本地图形资源：" + asset);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("SVG 内容为空。");
        var scene = new SvgMotionScene();
        foreach (var element in root.Elements()) scene.Add(element, scene.Drawing);
        return scene;
    }

    public void SetPlaying(bool playing)
    {
        if (IsRunning == playing) return;
        foreach (var track in _tracks)
        {
            track.Target.ApplyAnimationClock(track.Property, null);
            if (playing) track.Target.ApplyAnimationClock(track.Property, track.Animation.CreateClock());
        }
        IsRunning = playing;
    }

    private static double Number(string? value, double fallback = 0) => value is null ? fallback : double.Parse(value, CultureInfo.InvariantCulture);
    private static double N(XElement e, string name, double fallback = 0) => Number(e.Attribute(name)?.Value, fallback);
    private static SolidColorBrush? Paint(string? value)
    {
        if (value is null or "none") return null;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
    }
    private void Add(XElement element, DrawingGroup parent)
    {
        var group = new DrawingGroup { Opacity = N(element, "opacity", 1) };
        var kind = element.Name.LocalName;
        Geometry? geometry = kind switch
        {
            "path" => Geometry.Parse(element.Attribute("d")!.Value),
            "circle" => new EllipseGeometry(new(N(element, "cx"), N(element, "cy")), N(element, "r"), N(element, "r")),
            "rect" => new RectangleGeometry(new(N(element, "x"), N(element, "y"), N(element, "width"), N(element, "height")), N(element, "rx"), N(element, "rx")),
            _ => null
        };
        Pen? pen = null;
        if (geometry is not null)
        {
            geometry.Freeze();
            if (Paint(element.Attribute("stroke")?.Value) is { } stroke)
            {
                var thickness = N(element, "stroke-width", 1);
                pen = new(stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (element.Attribute("stroke-dasharray") is { } dash)
                    pen.DashStyle = new(dash.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => Number(v) / thickness), 0);
            }
            group.Children.Add(new GeometryDrawing(Paint(element.Attribute("fill")?.Value), pen, geometry));
        }
        else if (kind != "g") return;
        parent.Children.Add(group);
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            if (name == "animate" && child.Attribute("attributeName")?.Value == "opacity")
                AddTrack(child, group, DrawingGroup.OpacityProperty, 1);
            else if (name == "animate" && child.Attribute("attributeName")?.Value == "stroke-dashoffset" && pen is not null)
                AddTrack(child, pen.DashStyle, DashStyle.OffsetProperty, 1 / pen.Thickness);
            else if (name == "animateTransform" && child.Attribute("type")?.Value == "rotate")
            {
                var from = child.Attribute("from")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var rotation = new RotateTransform(Number(from[0]), Number(from[1]), Number(from[2])); group.Transform = rotation;
                var animation = new DoubleAnimation(Number(from[0]), Number(child.Attribute("to")!.Value.Split(' ')[0]), Seconds(child));
                Configure(child, animation); _tracks.Add(new(rotation, RotateTransform.AngleProperty, animation));
            }
            else Add(child, group);
        }
    }
    private static TimeSpan Seconds(XElement element) => TimeSpan.FromSeconds(Number(element.Attribute("dur")!.Value.TrimEnd('s')));
    private static void Configure(XElement element, Timeline animation)
    {
        animation.RepeatBehavior = RepeatBehavior.Forever;
        animation.BeginTime = TimeSpan.FromSeconds(Number(element.Attribute("begin")?.Value.TrimEnd('s')));
        Timeline.SetDesiredFrameRate(animation, 24);
    }
    private void AddTrack(XElement element, Animatable target, DependencyProperty property, double scale)
    {
        var values = element.Attribute("values")?.Value.Split(';').Select(v => Number(v) * scale).ToArray() ??
            [N(element, "from") * scale, N(element, "to") * scale];
        var duration = Seconds(element);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        for (var i = 0; i < values.Length; i++)
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromTicks(duration.Ticks * i / (values.Length - 1)))));
        Configure(element, animation); _tracks.Add(new(target, property, animation));
    }
}
