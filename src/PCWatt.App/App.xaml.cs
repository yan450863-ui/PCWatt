using System.IO;
using System.Windows;
using Microsoft.Win32;
using PCWatt.App.Services;
using PCWatt.App.ViewModels;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Persistence;
using PCWatt.Infrastructure.Power;
using PCWatt.Infrastructure.Time;
using PCWatt.Infrastructure.Windows;

namespace PCWatt.App;

public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private SamplingCoordinator? _sampling;
    private ApplicationPreferences? _preferences;
    private TrayService? _tray;
    private MainViewModel? _model;
    private WindowsDisplayStateProvider? _display;
    private OnDemandWindowSession? _windowSession;
    private string? _lastUiError;
    private string? _notificationStatus;
    private bool _exiting;
    private bool _openRequested;
    private readonly CancellationTokenSource _notificationLifetime = new();
    private Task? _notificationTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args is ["--package-check", var reportPath])
        {
            try { PackageDiagnostics.Verify(reportPath); Shutdown(); }
            catch { Shutdown(1); }
            return;
        }
        try
        {
            var integrity = WindowsLaunchEnvironment.GetIntegrityLevel();
            StartupDiagnostics.Write($"startup background={e.Args.Contains("--background")}; integrity=0x{integrity:X}; base={AppContext.BaseDirectory}");
            WindowsLaunchEnvironment.Validate(integrity,
                WindowsLaunchEnvironment.FindLaunchFile(AppContext.BaseDirectory));
            await AdministratorLaunch.WaitForHandoffAsync(e.Args, integrity);
            try { _instance = new SingleInstanceCoordinator(); }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("PC Watt 已在管理员模式下运行，请从系统托盘打开。", "PC Watt");
                Shutdown(); return;
            }
            if (!_instance.IsPrimary)
            {
                try { await _instance.NotifyPrimaryAsync(); }
                catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
                { MessageBox.Show("PC Watt 已在运行，请从系统托盘打开。", "PC Watt"); }
                Shutdown();
                return;
            }
            _instance.Listen(() => Dispatcher.BeginInvoke(() => { _openRequested = true; OpenMainWindow(); }));
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCWatt");
            var local = Path.Combine(directory, "appsettings.local.json");
            var store = new UserSettingsStore(Path.Combine(directory, "settings.json"));
            var settings = await store.LoadAsync(File.Exists(local) ? local : Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            await store.SaveAsync(settings);
            if (settings.RequestAdministratorOnLaunch && integrity < 0x3000)
            {
                try
                {
                    var elevationLauncher = new AutostartLauncherInstaller(AppContext.BaseDirectory, directory).Install();
                    if (AdministratorLaunch.Request(elevationLauncher, e.Args.Contains("--background")))
                    { Shutdown(); return; }
                }
                catch (Exception exception) { StartupDiagnostics.Write("完整硬件启动未完成，继续普通权限记录：" + exception); }
            }
            var databasePath = Path.Combine(directory, "simulation.db");
            var repository = new SqliteEnergyRepository(databasePath);
            await repository.InitializeAsync();
            var clock = new SystemClock();
            _display = new WindowsDisplayStateProvider(clock);
            _display.StateChanged += OnDisplayStateChanged;
            var tariffs = new SqliteTariffRepository(databasePath);
            if (settings.Tariff is { } legacy && (await tariffs.ReadAsync()).Count == 0)
            {
                var first = (await repository.ReadWindowAsync(MockPowerSource.SourceId, DateTimeOffset.MinValue, DateTimeOffset.MinValue)).FirstOrDefault();
                await tariffs.ImportLegacyAsync(legacy, LocalTimeRules.DateAt(first?.Timestamp ?? clock.UtcNow, TimeZoneInfo.Local), clock.UtcNow);
            }
            _sampling = new SamplingCoordinator(() => new HardwarePowerSource(new LibreHardwareMonitorAdapter(clock),
                () => _preferences is null ? settings.WholePcEstimate : _preferences.Current.WholePcEstimate,
                () => _preferences?.Current.AutomaticWholePcEstimate ?? settings.AutomaticWholePcEstimate), repository, settings.SamplingIntervalSeconds,
                maximumIntegrationInterval: () => _preferences?.Current.MaximumIntegrationGapSeconds ?? settings.MaximumIntegrationGapSeconds,
                discoverWhenPaused: true);
            if (settings.RecordingPaused) await _sampling.SetPausedAsync(true);
            var launcher = AutostartLauncherInstaller.LaunchPath(AppContext.BaseDirectory, directory);
            var autostart = new AutostartService(new CurrentUserAutostartStore(), launcher);
            string? startupWarning = null;
            try { new AutostartLauncherInstaller(AppContext.BaseDirectory, directory).Install(); autostart.SetEnabled(settings.AutoStartEnabled); StartupDiagnostics.Write("autostart registration ready: " + autostart.CurrentCommand); }
            catch (Exception exception) { startupWarning = $"自启动配置未生效：{exception.Message}"; StartupDiagnostics.Write(exception.ToString()); }
            _preferences = new ApplicationPreferences(settings, store, autostart, _sampling);
            var buffered = new BufferedEnergyRepository(repository, _sampling);
            // Only monitoring, settings storage and tray objects exist until the user opens a window.
            var notificationBilling = new WholePcBillingService(buffered, repository, () => _sampling.Channels, tariffs, clock,
                TimeZoneInfo.Local, () => TimeSpan.FromSeconds(_preferences.Current.MaximumIntegrationGapSeconds));
            _windowSession = new OnDemandWindowSession(() =>
            {
                var current = _preferences.Current;
                var energyHistory = new EnergyHistoryService(buffered, repository, () => _sampling.Channels, clock, TimeZoneInfo.Local,
                    () => TimeSpan.FromSeconds(_preferences.Current.MaximumIntegrationGapSeconds), () => _preferences.Current.WholePcEstimate);
                var dashboard = new ChannelDashboardService(buffered, repository, _sampling, tariffs, clock, TimeZoneInfo.Local,
                    () => TimeSpan.FromSeconds(_preferences.Current.MaximumIntegrationGapSeconds), MockPowerSource.SourceId,
                    () => _sampling.Channels.FirstOrDefault(c => c.Kind == PCWatt.Core.Models.PowerComponentKind.WholePc &&
                        c.Availability != PCWatt.Core.Models.PowerAvailability.Disconnected)?.Id ?? WholePcPowerEstimator.UnknownSourceId,
                    energyHistory, () => _preferences.Current.WholePcEstimate)
                    { SelectedSourceId = current.SelectedPowerSource?.StartsWith("mock:", StringComparison.Ordinal) == true ? null : current.SelectedPowerSource };
                var billing = new WholePcBillingService(buffered, repository, () => _sampling.Channels, tariffs, clock,
                    TimeZoneInfo.Local, () => TimeSpan.FromSeconds(_preferences.Current.MaximumIntegrationGapSeconds));
                var model = new MainViewModel(dashboard, new CostCalculator(), null, clock, TimeZoneInfo.Local,
                    TimeSpan.FromSeconds(current.MaximumIntegrationGapSeconds), () => !_sampling.IsPaused, ToggleRecordingAsync,
                    current.VisiblePowerSources, (selected, visible) => _preferences.UpdateAsync(current => current with
                        { SelectedPowerSource = selected, VisiblePowerSources = visible }), current.ShowWholePc,
                    mode => _preferences.UpdateAsync(current => current with { ShowWholePc = mode }), billing, _sampling, _sampling,
                    energyHistory, _display);
                var history = new TariffHistoryService(tariffs, clock, TimeZoneInfo.Local);
                model.Settings = new SettingsViewModel(() => _preferences.Current, _preferences.UpdateAsync,
                    () => tariffs.ReadAsync(), (date, tariff) => history.AddAsync(date, tariff),
                    () => billing.GetDateAsync(LocalTimeRules.DateAt(clock.UtcNow, TimeZoneInfo.Local).AddDays(-1)),
                    () => model.RefreshAsync(false, CancellationToken.None), () => dashboard.Channels,
                    async () =>
                    {
                        if (WindowsLaunchEnvironment.GetIntegrityLevel() >= 0x3000) return;
                        var elevationLauncher = new AutostartLauncherInstaller(AppContext.BaseDirectory, directory).Install();
                        if (AdministratorLaunch.Request(elevationLauncher, false)) await ExitAsync();
                    }, integrity >= 0x3000);
                _model = model;
                if (_notificationStatus is not null) model.Settings.SetNotificationStatus(_notificationStatus);
                if (_lastUiError is not null) model.ReportError(_lastUiError);
                var window = new MainWindow(model, current.SamplingIntervalSeconds, current.MaximumIntegrationGapSeconds,
                    _sampling, () => _tray is not null && _preferences.Current.CloseToTray, ExitAsync, ReleaseMainWindow) { Icon = BrandIcon.Image() };
                window.ApplyPreferences(current.MotionEnabled, current.SamplingIntervalSeconds, current.MaximumIntegrationGapSeconds);
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(MainWindow, window)) { MainWindow = null; _model = null; }
                };
                return window;
            });
            _preferences.Changed += OnPreferencesChanged;
            try { _tray = new TrayService(_sampling, OpenMainWindow, OpenSettings, ToggleRecordingAsync, ExitAsync,
                ReportUiError, () => _preferences.Current.ShowWholePc ?
                    _sampling.Channels.FirstOrDefault(c => c.Kind == PCWatt.Core.Models.PowerComponentKind.WholePc)?.Id : _preferences.Current.SelectedPowerSource,
                () => TrayStatusFormatter.Format(new(clock.UtcNow, _sampling.Channels, _sampling.LatestSamples) { Temperatures = _sampling.Temperatures },
                    _sampling.IsPaused, _sampling.IsSuspended, _sampling.LastError is not null, clock.UtcNow,
                    TimeSpan.FromSeconds(Math.Max(_preferences.Current.MaximumIntegrationGapSeconds, _sampling.IntervalSeconds * 3)))); }
            catch (Exception exception) { startupWarning = $"系统托盘不可用，关闭窗口将完全退出：{exception.Message}"; }
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _sampling.Start();
            StartupDiagnostics.Write("ready; sampling started; tray=" + (_tray is not null));
            if (_tray is not null)
            {
                var notifications = new StartupNotificationService(clock, TimeZoneInfo.Local,
                    new SqliteNotificationHistory(databasePath), _tray, notificationBilling.GetDateAsync);
                _notificationTask = Task.Run(async () =>
                {
                    var message = await notifications.RunAsync(settings.StartNotificationEnabled, settings.YesterdayReportEnabled,
                        settings.RecordingPaused, _notificationLifetime.Token);
                    if (message is not null && !_notificationLifetime.IsCancellationRequested)
                        await Dispatcher.InvokeAsync(() => { _notificationStatus = message; _model?.Settings?.SetNotificationStatus(message); });
                });
            }
            if (!e.Args.Contains("--background") || _tray is null || _openRequested) OpenMainWindow();
            if (startupWarning is not null) ReportUiError(startupWarning);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write(exception.ToString());
            MessageBox.Show($"启动失败：{exception.Message}\n\n已有配置和数据库已保留。", "PC Watt", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_sampling is not null) { try { await _sampling.StopAsync(); } catch { } }
            Shutdown(1);
        }
    }

    private void OpenMainWindow()
    {
        if (_exiting || _windowSession is null) return;
        var window = _windowSession.Open();
        MainWindow = window;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    private void OpenSettings() { OpenMainWindow(); _model?.ShowSettingsCommand.Execute(null); }
    private void ReportUiError(string message) { _lastUiError = message; _model?.ReportError(message); }
    private void OnDisplayStateChanged(object? sender, EventArgs e)
    {
        // Display notifications keep working with zero dashboard windows; never create UI here.
        if (_exiting || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() => _model?.NotifyDisplayState());
    }
    private void ReleaseMainWindow()
    {
        if (MainWindow is not MainWindow window || _exiting) return;
        _windowSession?.Release();
        StartupDiagnostics.Write("view released; monitoring continues; windows=" + Windows.Count);
    }
    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        if (_preferences is null || MainWindow is not MainWindow window) return;
        var current = _preferences.Current;
        window.ApplyPreferences(current.MotionEnabled, current.SamplingIntervalSeconds, current.MaximumIntegrationGapSeconds);
    }
    private Task ToggleRecordingAsync() => _preferences is null || _sampling is null ? Task.CompletedTask :
        _preferences.UpdateAsync(current => current with { RecordingPaused = !_sampling.IsPaused });

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_sampling is null || _exiting || e.Mode == PowerModes.StatusChange) return;
        try { await _sampling.SetSuspendedAsync(e.Mode == PowerModes.Suspend); }
        catch (Exception exception) { await Dispatcher.InvokeAsync(() => _model?.ReportError($"电源状态切换：{exception.Message}")); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if (_preferences is not null) await _preferences.WaitForIdleAsync();
            if (_sampling is not null) await _sampling.StopAsync();
            _notificationLifetime.Cancel();
            if (_notificationTask is not null) await _notificationTask;
        }
        catch (Exception exception)
        {
            _exiting = false;
            OpenMainWindow();
            _model?.ReportError($"退出前保存失败，程序仍在运行，请重试退出：{exception.Message}");
            return;
        }
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_preferences is not null) _preferences.Changed -= OnPreferencesChanged;
        _windowSession?.Release();
        _windowSession = null;
        _tray?.Dispose();
        _tray = null;
        (MainWindow as MainWindow)?.CloseForExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_display is not null) { _display.StateChanged -= OnDisplayStateChanged; _display.Dispose(); }
        _notificationLifetime.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_sampling is not null)
        {
            try
            {
                // Also covers Windows session shutdown; normal tray exit has already flushed.
                var cleanup = Task.Run(async () => await _sampling.DisposeAsync());
                if (!cleanup.Wait(TimeSpan.FromSeconds(5))) System.Diagnostics.Trace.WriteLine("退出保存超过 5 秒，未完成区间将在下次启动标记为缺失。");
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine($"退出保存失败：{exception.Message}"); }
        }
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
