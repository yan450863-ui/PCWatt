using System.Windows;
using System.Windows.Media;

namespace PCWatt.App.Controls;

public sealed partial class PowerTrend
{
    // Shared convex interpolation weights keep every stacked band nonnegative and bounded.
    // Smooth display geometry never becomes a sample or alters the recorded bucket integral.
    internal static (Point First, Point Second) WaveControls(Point a, Point b) =>
        (new(a.X + (b.X - a.X) * .42, a.Y), new(b.X - (b.X - a.X) * .42, b.Y));
    private static void ContinueWave(StreamGeometryContext context, IReadOnlyList<Point> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var (first, second) = WaveControls(points[i - 1], points[i]);
            context.BezierTo(first, second, points[i], true, false);
        }
    }
    internal static StreamGeometry WaveLine(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        if (points.Count > 0)
            using (var context = geometry.Open()) { context.BeginFigure(points[0], false, false); ContinueWave(context, points); }
        geometry.Freeze(); return geometry;
    }
    internal static StreamGeometry WaveBand(IReadOnlyList<Point> upper, IReadOnlyList<Point> lower)
    {
        var geometry = new StreamGeometry();
        if (upper.Count > 1)
            using (var context = geometry.Open())
            {
                context.BeginFigure(upper[0], true, true); ContinueWave(context, upper);
                var reverse = lower.Reverse().ToArray(); context.LineTo(reverse[0], true, false); ContinueWave(context, reverse);
            }
        geometry.Freeze(); return geometry;
    }
}
