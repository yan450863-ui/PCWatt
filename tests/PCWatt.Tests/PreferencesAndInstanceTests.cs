using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Services;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Windows;

namespace PCWatt.Tests;

[TestClass]
public sealed class PreferencesAndInstanceTests
{
    private sealed class FakeAutostart : IAutostartStore
    {
        public string? Command = "\"D:\\previous location\\PCWatt.App.exe\" --background";
        public string? Read() => Command;
        public void Write(string command) => Command = command;
        public void Remove() => Command = null;
    }
    private sealed class FakeStore : IUserSettingsStore
    {
        public bool Fail;
        public AppSettings? Saved;
        public Action? AtSave;
        public TaskCompletionSource? Hold;
        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            AtSave?.Invoke();
            if (Fail) throw new IOException("测试设置保存失败");
            if (Hold is { } hold) { Hold = null; await hold.Task; }
            Saved = settings;
        }
    }
    private sealed class FakeSampling : ISamplingController
    {
        public bool IsPaused { get; private set; }
        public int IntervalSeconds { get; private set; } = 30;
        public int IntervalChanges;
        public string? LastError => null;
        public PowerSample? LatestSample => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetPausedAsync(bool paused) { IsPaused = paused; return Task.CompletedTask; }
        public Task ChangeIntervalAsync(int seconds) { IntervalChanges++; IntervalSeconds = seconds; return Task.CompletedTask; }
    }

    [TestMethod]
    public async Task ConcurrentPreferenceUpdatesPreserveIndependentFieldsAndDoNotResetSampling()
    {
        var registration = new FakeAutostart();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore { Hold = hold };
        var sampling = new FakeSampling();
        var preferences = new ApplicationPreferences(new AppSettings(), store, new AutostartService(registration, @"D:\PCWatt.App.exe"), sampling);
        var motion = preferences.UpdateAsync(current => current with { MotionEnabled = false });
        var autostart = preferences.UpdateAsync(current => current with { AutoStartEnabled = false });
        hold.SetResult();
        await Task.WhenAll(motion, autostart);
        Assert.IsFalse(preferences.Current.MotionEnabled);
        Assert.IsFalse(preferences.Current.AutoStartEnabled);
        Assert.AreEqual(0, sampling.IntervalChanges);
    }

    [TestMethod]
    public void AutostartEnableQuotesExecutableAndDisableRemovesOnlyOwnedRegistration()
    {
        var store = new FakeAutostart();
        var service = new AutostartService(store, @"D:\a folder\PCWatt.App.exe");
        service.SetEnabled(true);
        Assert.AreEqual("\"D:\\a folder\\PCWatt.App.exe\" --background", store.Command);
        service.SetEnabled(false);
        Assert.IsNull(store.Command);
        service.SetEnabled(false);
        Assert.IsNull(store.Command);
        Assert.IsTrue(new AppSettings().AutoStartEnabled);
    }

    [TestMethod]
    public void AutostartUsesTheSameQuotedLauncherAsManualStartup()
    {
        var store = new FakeAutostart();
        var launcher = @"D:\a folder\PCWatt\Launch.cmd";
        var service = new AutostartService(store, launcher);
        service.SetEnabled(true);
        var commandHost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Assert.AreEqual($"\"{commandHost}\" /d /c \"\"{launcher}\" --background\"", store.Command);
        Assert.IsLessThanOrEqualTo(260, store.Command!.Length);
        service.SetEnabled(false);
        Assert.IsNull(store.Command);
    }

    [TestMethod]
    public void InvalidAutostartExecutableDoesNotOverwriteExistingValue()
    {
        var store = new FakeAutostart();
        var original = store.Command;
        Assert.Throws<InvalidOperationException>(() => new AutostartService(store, "relative.exe").SetEnabled(true));
        Assert.AreEqual(original, store.Command);
    }

    [TestMethod]
    public async Task TurningOffAutostartRemovesItBeforeSettingsSaveAndDoesNotPauseRecording()
    {
        var registration = new FakeAutostart();
        var store = new FakeStore { AtSave = () => Assert.IsNull(registration.Command) };
        var sampling = new FakeSampling();
        var preferences = new ApplicationPreferences(new AppSettings(), store, new AutostartService(registration, @"D:\PCWatt.App.exe"), sampling);
        await preferences.ApplyAsync(preferences.Current with { AutoStartEnabled = false });
        Assert.IsFalse(preferences.Current.AutoStartEnabled);
        Assert.IsFalse(sampling.IsPaused);
    }

    [TestMethod]
    public async Task FailedSettingsWriteRestoresExactAutostartValueAndPreviousSettings()
    {
        var registration = new FakeAutostart();
        var original = registration.Command;
        var store = new FakeStore { Fail = true };
        var sampling = new FakeSampling();
        var preferences = new ApplicationPreferences(new AppSettings(), store, new AutostartService(registration, @"D:\PCWatt.App.exe"), sampling);
        await Assert.ThrowsAsync<IOException>(() => preferences.ApplyAsync(preferences.Current with { AutoStartEnabled = false, SamplingIntervalSeconds = 5 }));
        Assert.AreEqual(original, registration.Command);
        Assert.IsTrue(preferences.Current.AutoStartEnabled);
        Assert.AreEqual(30, sampling.IntervalSeconds);
    }

    [TestMethod]
    public async Task PreferencesRoundTripKeepsIndependentFlagsAndVersionedSettings()
    {
        using var scope = new TemporaryRepository();
        var path = scope.DatabasePath + ".settings.json";
        var settings = new AppSettings
        {
            AutoStartEnabled = false, StartNotificationEnabled = true, YesterdayReportEnabled = false,
            CloseToTray = true, MotionEnabled = false, SamplingIntervalSeconds = 30, RecordingPaused = true
        };
        var store = new UserSettingsStore(path);
        await store.SaveAsync(settings);
        var restored = await new UserSettingsStore(path).LoadAsync("unused-fallback.json");
        Assert.AreEqual(settings, restored);
        Assert.IsTrue(restored.StartNotificationEnabled);
        Assert.IsFalse(restored.YesterdayReportEnabled);
        Assert.DoesNotContain("Tariff", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task UnknownSettingsVersionIsReportedAndOriginalIsPreserved()
    {
        using var scope = new TemporaryRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        var path = scope.DatabasePath + ".settings.json";
        const string original = "{\"SchemaVersion\":99,\"Settings\":{}}";
        await File.WriteAllTextAsync(path, original);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UserSettingsStore(path).LoadAsync("unused.json"));
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task LegacyConfigurationIsLoadedWithoutOverwritingTheLegacyFile()
    {
        using var scope = new TemporaryRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        var legacy = scope.DatabasePath + ".legacy.json";
        const string original = "{\"SamplingIntervalSeconds\":5,\"CurrencyCode\":\"EUR\",\"PricePerKilowattHour\":0.2}";
        await File.WriteAllTextAsync(legacy, original);
        var store = new UserSettingsStore(scope.DatabasePath + ".settings.json");
        var settings = await store.LoadAsync(legacy);
        await store.SaveAsync(settings);
        Assert.AreEqual(5, settings.SamplingIntervalSeconds);
        Assert.AreEqual("EUR", settings.Tariff!.CurrencyCode);
        Assert.AreEqual(original, await File.ReadAllTextAsync(legacy));
    }

    [TestMethod]
    public void SecondaryInstanceCannotOwnMutexAndCanRequestOpeningPrimaryWindow()
    {
        var identity = "PCWatt.Tests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(identity);
        Assert.IsTrue(primary.IsPrimary);
        using var opened = new ManualResetEventSlim();
        primary.Listen(() => opened.Set());
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var secondary = new SingleInstanceCoordinator(identity);
                Assert.IsFalse(secondary.IsPrimary);
                secondary.NotifyPrimaryAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.IsTrue(opened.Wait(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public void InstanceLeaseCanBeReacquiredAfterOwnerExits()
    {
        var identity = "PCWatt.Tests." + Guid.NewGuid().ToString("N");
        using (var first = new SingleInstanceCoordinator(identity)) Assert.IsTrue(first.IsPrimary);
        using var next = new SingleInstanceCoordinator(identity);
        Assert.IsTrue(next.IsPrimary);
    }
}
