using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Windows;

/// <summary>One native subscription; no polling, hardware driver, HWND or power samples.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayStateProvider : IDisplayStateProvider, IDisposable
{
    internal static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private readonly IClock _clock;
    private readonly IDisplayNotifications _notifications;
    private DisplayStatus _status = new(DisplayPowerState.Unknown, null);
    private int _disposed;
    public DisplayStatus Status => Volatile.Read(ref _status);
    public string DisplayName { get; }
    public string? Error { get; }
    public event EventHandler? StateChanged;

    public WindowsDisplayStateProvider(IClock clock) : this(clock, new NativeDisplayNotifications(), ReadDisplayName) { }

    internal WindowsDisplayStateProvider(IClock clock, IDisplayNotifications notifications, Func<string> readName)
    {
        _clock = clock;
        _notifications = notifications;
        try { DisplayName = readName(); }
        catch { DisplayName = "显示器"; }
        try
        {
            var result = notifications.Register(OnNotification);
            if (result != 0) Error = $"Windows 显示状态不可用（{result}）";
        }
        catch (Exception exception) { Error = "Windows 显示状态不可用：" + exception.Message; }
        if (Error is not null)
        {
            _status = new(DisplayPowerState.Unknown, null);
            notifications.Dispose();
        }
    }

    private void OnNotification(uint type, IntPtr payload)
    {
        if (Volatile.Read(ref _disposed) != 0 || !TryDecode(type, payload, out var state)) return;
        var previous = Interlocked.Exchange(ref _status, new(state, _clock.UtcNow));
        if (previous.State == state) return;
        // Subscribers cannot let an exception cross the unmanaged callback boundary.
        foreach (EventHandler handler in StateChanged?.GetInvocationList() ?? [])
            try { handler(this, EventArgs.Empty); } catch { }
    }

    internal static bool TryDecode(uint type, IntPtr payload, out DisplayPowerState state)
    {
        state = DisplayPowerState.Unknown;
        if (type != 0x8013 || payload == IntPtr.Zero) return false; // PBT_POWERSETTINGCHANGE
        if (Marshal.PtrToStructure<Guid>(payload) != SessionDisplayStatus || Marshal.ReadInt32(payload, 16) != 4) return false;
        state = Marshal.ReadInt32(payload, 20) switch
        { 0 => DisplayPowerState.Off, 1 => DisplayPowerState.On, 2 => DisplayPowerState.Dimmed, _ => DisplayPowerState.Unknown };
        return true;
    }

    private static string ReadDisplayName()
    {
        using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT UserFriendlyName FROM WmiMonitorID WHERE Active = TRUE");
        searcher.Options.Timeout = TimeSpan.FromSeconds(3);
        using var results = searcher.Get();
        var names = new List<string>();
        foreach (ManagementObject item in results)
        {
            using (item)
                if (item["UserFriendlyName"] is ushort[] chars)
                {
                    var name = new string(chars.Where(c => c >= 32 && c != 127).Select(c => (char)c).ToArray()).Trim();
                    if (name.Length > 0) names.Add(name);
                }
        }
        return names.Count == 0 ? "显示器" : string.Join(" / ", names.Distinct());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _notifications.Dispose();
        StateChanged = null;
    }
}

internal interface IDisplayNotifications : IDisposable
{
    uint Register(Action<uint, IntPtr> notification);
}

[SupportedOSPlatform("windows")]
internal sealed class NativeDisplayNotifications : IDisplayNotifications
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint NotifyCallback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)]
    private struct Subscription { public IntPtr Callback; public IntPtr Context; }
    private NotifyCallback? _callback;
    private GCHandle _callbackRoot;
    private IntPtr _recipient;
    private IntPtr _registration;

    public uint Register(Action<uint, IntPtr> notification)
    {
        _callback = (_, type, setting) =>
        {
            try { notification(type, setting); } catch { }
            return 0;
        };
        _callbackRoot = GCHandle.Alloc(_callback);
        _recipient = Marshal.AllocHGlobal(Marshal.SizeOf<Subscription>());
        Marshal.StructureToPtr(new Subscription { Callback = Marshal.GetFunctionPointerForDelegate(_callback) }, _recipient, false);
        var guid = WindowsDisplayStateProvider.SessionDisplayStatus;
        return PowerSettingRegisterNotification(ref guid, 2, _recipient, out _registration); // DEVICE_NOTIFY_CALLBACK
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            // Keep callback and recipient rooted until Windows has cancelled the subscription.
            var result = PowerSettingUnregisterNotification(_registration);
            if (result != 0) return;
            _registration = IntPtr.Zero;
        }
        if (_recipient != IntPtr.Zero) { Marshal.FreeHGlobal(_recipient); _recipient = IntPtr.Zero; }
        GC.KeepAlive(_callback);
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        _callback = null;
    }

    [DllImport("Powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags, IntPtr recipient, out IntPtr registration);
    [DllImport("Powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registration);
}
