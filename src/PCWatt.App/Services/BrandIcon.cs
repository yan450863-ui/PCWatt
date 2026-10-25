using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCWatt.App.Services;

public static class BrandIcon
{
    public static BitmapSource Image()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 118, 110)), null, new Rect(1, 1, 62, 62), 17, 17);
            var bolt = Geometry.Parse("M36,8 L18,35 L30,35 L24,56 L49,26 L35,26 Z");
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(255, 255, 255)), null, bolt);
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static System.Drawing.Icon TrayIcon()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Image()));
        using var png = new MemoryStream();
        encoder.Save(png);
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)64); writer.Write((byte)64); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((int)png.Length); writer.Write(22);
            writer.Write(png.ToArray());
        }
        ico.Position = 0;
        using var icon = new System.Drawing.Icon(ico);
        return (System.Drawing.Icon)icon.Clone();
    }
}
