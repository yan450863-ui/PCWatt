using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Infrastructure.Windows;

namespace PCWatt.Tests;

[TestClass]
public sealed class DisplayStateTests
{
    [TestMethod]
    public void CurrentStateArrivesDuringRegistrationWithoutAWindowOrPolling()
    {
        var native = new FakeNotifications { Initial = 1 };
        using var provider = new WindowsDisplayStateProvider(new FakeClock(TestData.Origin), native, () => "HKC 测试显示器");
        Assert.AreEqual(DisplayPowerState.On, provider.Status.State);
        Assert.AreEqual(TestData.Origin, provider.Status.ObservedAt);
        Assert.AreEqual("HKC 测试显示器", provider.DisplayName);
        Assert.AreEqual(1, native.RegisterCalls);
        Assert.IsNull(provider.Error);
    }

    [TestMethod]
    public void OnOffDimAndUnknownAreDistinctAndRepeatedStatesDoNotNotifyAgain()
    {
        var native = new FakeNotifications(); var clock = new FakeClock(TestData.Origin);
        using var provider = new WindowsDisplayStateProvider(clock, native, () => "测试");
        var changes = 0; provider.StateChanged += (_, _) => changes++;
        native.Send(1); native.Send(1); Assert.AreEqual(1, changes);
        clock.UtcNow = clock.UtcNow.AddSeconds(1); native.Send(0);
        Assert.AreEqual(DisplayPowerState.Off, provider.Status.State);
        Assert.AreEqual(clock.UtcNow, provider.Status.ObservedAt);
        native.Send(2); Assert.AreEqual(DisplayPowerState.Dimmed, provider.Status.State);
        native.Send(99); Assert.AreEqual(DisplayPowerState.Unknown, provider.Status.State);
        Assert.AreEqual(4, changes);
    }

    [TestMethod]
    public void UnrelatedOrMalformedNotificationsAreIgnored()
    {
        Assert.IsFalse(WindowsDisplayStateProvider.TryDecode(0x8013, IntPtr.Zero, out _));
        var native = new FakeNotifications { Initial = 1 };
        using var provider = new WindowsDisplayStateProvider(new FakeClock(TestData.Origin), native, () => "测试");
        native.Send(0, type: 7); native.Send(0, length: 8); native.Send(0, guid: Guid.Empty);
        Assert.AreEqual(DisplayPowerState.On, provider.Status.State);
    }

    [TestMethod]
    public void SubscriptionAndInventoryFailuresLeaveUnknownWithoutCrashing()
    {
        var native = new FakeNotifications { Error = 5 };
        using var provider = new WindowsDisplayStateProvider(new FakeClock(TestData.Origin), native, () => throw new InvalidOperationException("WMI 不可用"));
        Assert.AreEqual("显示器", provider.DisplayName);
        Assert.AreEqual(DisplayPowerState.Unknown, provider.Status.State);
        Assert.IsNull(provider.Status.ObservedAt);
        Assert.Contains("5", provider.Error!);
        Assert.AreEqual(1, native.DisposeCalls);
    }

    [TestMethod]
    public void DisposeUnsubscribesOnceAndLateEventsCannotUpdateState()
    {
        var native = new FakeNotifications { Initial = 1 };
        var provider = new WindowsDisplayStateProvider(new FakeClock(TestData.Origin), native, () => "测试");
        provider.Dispose(); provider.Dispose(); native.Send(0);
        Assert.AreEqual(1, native.DisposeCalls);
        Assert.AreEqual(DisplayPowerState.On, provider.Status.State);
    }

    [TestMethod]
    public void SubscriberFailureDoesNotBlockOtherSubscribersOrEscapeToWindows()
    {
        var native = new FakeNotifications();
        using var provider = new WindowsDisplayStateProvider(new FakeClock(TestData.Origin), native, () => "测试");
        provider.StateChanged += (_, _) => throw new InvalidOperationException();
        var notified = false; provider.StateChanged += (_, _) => notified = true;
        native.Send(1); Assert.IsTrue(notified);
    }

    [TestMethod]
    public async Task ScreenOffStopsMotionButDoesNotChangeEnergyOrCostAndCanRestoreUi()
    {
        var display = new FixtureDisplay(); var model = WholePcBillingTests.CreateModel(display: display);
        await model.RefreshAsync(false, CancellationToken.None);
        var cost = model.TodayCost; var energy = model.TodayBillEnergy;
        display.Set(DisplayPowerState.On); model.NotifyDisplayState();
        Assert.AreEqual("亮屏", model.DisplayStateText); Assert.IsTrue(model.RenderMotionEnabled);
        display.Set(DisplayPowerState.Off); model.NotifyDisplayState();
        Assert.AreEqual("熄屏", model.DisplayStateText); Assert.IsFalse(model.RenderMotionEnabled);
        Assert.AreEqual(cost, model.TodayCost); Assert.AreEqual(energy, model.TodayBillEnergy);
        Assert.IsTrue(model.IsSampling); Assert.Contains("未计费", model.DisplayPowerHint);
        model.SetPresentationActive(false); display.Set(DisplayPowerState.On); model.NotifyDisplayState();
        Assert.IsFalse(model.RenderMotionEnabled);
        model.SetPresentationActive(true); Assert.IsTrue(model.RenderMotionEnabled);
        Assert.HasCount(5, model.PowerCategories);
    }

    internal sealed class FixtureDisplay : IDisplayStateProvider
    {
        public DisplayStatus Status { get; private set; } = new(DisplayPowerState.On, TestData.Origin);
        public string DisplayName => "HKC · 测试显示器";
        public string? Error => null;
        public event EventHandler? StateChanged;
        public void Set(DisplayPowerState state) { Status = new(state, TestData.Origin); StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeNotifications : IDisplayNotifications
    {
        public int? Initial; public uint Error; public int RegisterCalls, DisposeCalls;
        private Action<uint, IntPtr>? _notification;
        public uint Register(Action<uint, IntPtr> notification)
        { RegisterCalls++; _notification = notification; if (Initial is { } value) Send(value); return Error; }
        public void Send(int state, uint type = 0x8013, int length = 4, Guid? guid = null)
        {
            var pointer = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.StructureToPtr(guid ?? WindowsDisplayStateProvider.SessionDisplayStatus, pointer, false);
                Marshal.WriteInt32(pointer, 16, length); Marshal.WriteInt32(pointer, 20, state);
                _notification?.Invoke(type, pointer);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public void Dispose() { DisposeCalls++; }
    }
}
