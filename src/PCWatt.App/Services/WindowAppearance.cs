using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCWatt.App.Services;

internal static class WindowAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        // 浅色暖白主题：标题栏属性保持浅色（0），不支持时保持系统默认。
        var light = 0;
        if (DwmSetWindowAttribute(handle, 20, ref light, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref light, sizeof(int));

        // Windows 11 圆角（DWMWA_WINDOW_CORNER_PREFERENCE = 33，DWMWCP_ROUND = 2）。
        var round = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));

        // 自绘标题栏使用 WindowChrome 后没有原生边框，需要 1 像素 DWM 边框恢复系统阴影与圆角描边。
        // COLORREF 0x00BBGGRR = #E3DDD2 暖灰描边。
        var borderColor = 0x00D2DDE3;
        _ = DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);
    }
}
