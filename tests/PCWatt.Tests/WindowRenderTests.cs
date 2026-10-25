using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App;
using PCWatt.Core.Models;

namespace PCWatt.Tests;

[TestClass]
public sealed class WindowRenderTests
{
    [TestMethod]
    public void DashboardXamlRendersAtDefaultAndMinimumSizes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new PCWatt.App.App();
                Assert.AreEqual(0, application.Resources.Count, "监控启动不预加载界面主题。");
                var model = new PCWatt.App.ViewModels.MainViewModel(new DashboardViewModelTests.FakeDashboard(),
                    new PCWatt.Core.Services.CostCalculator(), null, new FakeClock(TestData.Origin),
                    TimeZoneInfo.Local, TimeSpan.FromSeconds(30));
                model.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                var preferences = new PCWatt.Infrastructure.Configuration.AppSettings();
                model.Settings = new PCWatt.App.ViewModels.SettingsViewModel(() => preferences,
                    update => { preferences = update(preferences); return Task.CompletedTask; },
                    () => Task.FromResult<IReadOnlyList<TariffVersion>>([new(Guid.NewGuid(), new(2026, 10, 23), new("CNY", 0.6m), TestData.Origin)]),
                    yesterday: () => Task.FromResult(NotificationTests.Report(new(2026, 10, 23))))
                { PriceInput = "0.6", EffectiveDate = new(2026, 10, 24) };
                var window = new MainWindow(model, 30, 30);
                model.UpdatePreferences(false, 30, 30);
                // Render only; never starts a window, timer, hardware source or real user database.
                var content = (FrameworkElement)window.Content;
                var root = FindProjectRoot();
                var directory = Path.Combine(root, "artifacts", "ui");
                Directory.CreateDirectory(directory);
                foreach (var (width, height, name) in new[] { (1164, 810, "dashboard.png"), (944, 660, "dashboard-minimum.png"),
                    (1164, 810, "settings.png"), (1164, 810, "settings-tariffs.png"), (944, 660, "settings-tariffs-minimum.png"), (1164, 810, "settings-report.png") })
                {
                    if (name == "settings.png") model.ShowSettingsCommand.Execute(null);
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, height));
                    content.UpdateLayout();
                    if (name.StartsWith("settings-", StringComparison.Ordinal))
                    {
                        FrameworkElement target;
                        if (name == "settings-report.png")
                        {
                            Descendants<System.Windows.Controls.Expander>(content).Single(e => e.Header?.ToString() == "通知与昨日报告（可选）").IsExpanded = true;
                            content.UpdateLayout();
                            var expander = Descendants<System.Windows.Controls.Expander>(content)
                                .Single(e => e.Header?.ToString()?.StartsWith("查看昨日报告", StringComparison.Ordinal) == true);
                            expander.IsExpanded = true;
                            target = expander;
                        }
                        else target = (FrameworkElement)window.FindName("TariffEditorHeading");
                        content.UpdateLayout();
                        var scroll = Descendants<System.Windows.Controls.ScrollViewer>(content).First();
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + target.TranslatePoint(new Point(), scroll).Y - 24);
                        content.UpdateLayout();
                    }
                    Assert.IsTrue(content.ActualWidth > 0);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, name));
                    encoder.Save(file);
                }
                window.Close();
                var money = WholePcBillingTests.CreateModel(temperatures: TemperatureTests.FixtureTemperatures(), utilization: UtilizationAndTrayTests.FixtureUsage(), history: new HistoryStatisticsTests.FixtureHistory(), display: new DisplayStateTests.FixtureDisplay());
                money.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                var moneyWindow = new MainWindow(money, 30, 30); money.UpdatePreferences(false, 30, 30);
                foreach (var (width, height, name) in new[] { (1104, 760, "period-chart-day-fixture.png"), (944, 660, "period-chart-day-minimum-fixture.png") })
                {
                    var panel = (FrameworkElement)moneyWindow.Content;
                    panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
                    Assert.AreEqual("0.80", ((System.Windows.Controls.TextBlock)moneyWindow.FindName("BillingCostValue")).Text);
                    Assert.AreEqual("48 °C", ((System.Windows.Controls.TextBlock)moneyWindow.FindName("CpuTemperatureValue")).Text);
                    Assert.AreEqual("12.5%", ((System.Windows.Controls.TextBlock)moneyWindow.FindName("CpuUtilizationValue")).Text);
                    Assert.AreEqual("25.0%", ((System.Windows.Controls.TextBlock)moneyWindow.FindName("GpuUtilizationValue")).Text);
                    Assert.AreEqual("65.0%", ((System.Windows.Controls.TextBlock)moneyWindow.FindName("MemoryUtilizationValue")).Text);
                    Assert.AreEqual("亮屏", ((System.Windows.Controls.TextBlock)moneyWindow.FindName("DisplayStateValue")).Text);
                    var chart = (FrameworkElement)moneyWindow.FindName("Trend");
                    Assert.IsTrue(((PCWatt.App.Controls.PowerTrend)chart).IsHistoryMode);
                    Assert.IsFalse(((PCWatt.App.Controls.SvgMotion)moneyWindow.FindName("MoneyMotion")).IsAnimating, "布局渲染不应启动动效。");
                    Assert.IsFalse(((PCWatt.App.Controls.SvgMotion)moneyWindow.FindName("HardwareMotion")).IsAnimating);
                    Assert.AreEqual(EnergyPeriod.Day, ((PCWatt.App.Controls.PowerTrend)chart).History!.Period);
                    Assert.IsTrue(chart.TranslatePoint(new Point(0, chart.ActualHeight), panel).Y < height - 30, "默认与最小窗口均应展示完整功耗曲线。");
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
                    var label = new DrawingVisual();
                    using (var draw = label.RenderOpen())
                        draw.DrawText(new FormattedText("界面预览 · 测试数据，非真实测量", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface("Microsoft YaHei UI"), 12, Brushes.Gold, 1), new Point(width - 295, height - 24));
                    bitmap.Render(label);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
                }
                money.SelectCategoryCommand.ExecuteAsync("whole").GetAwaiter().GetResult();
                // 日 / 月 / 年共用电费卡与整机分色柱，总量折线同步变化。
                foreach (var (period, name) in new[] { ("今日", "stacked-chart-day-fixture.png"), ("本月", "stacked-chart-month-fixture.png"), ("今年", "stacked-chart-year-fixture.png") })
                {
                    var command = period == "今日" ? money.ShowDayBillingCommand : period == "本月" ? money.ShowMonthBillingCommand : money.ShowYearBillingCommand;
                    command.Execute(null);
                    money.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                    var panel = (FrameworkElement)moneyWindow.Content;
                    panel.Measure(new Size(1104, 760)); panel.Arrange(new Rect(0, 0, 1104, 760)); panel.UpdateLayout();
                    var choice = Descendants<System.Windows.Controls.RadioButton>(panel).Single(r => r.Content?.ToString() == period);
                    Assert.IsTrue(choice.IsChecked == true, $"切换后应高亮“{period}”。");
                    Assert.Contains(period + "至今覆盖", money.BillingCoverage);
                    var trend = (PCWatt.App.Controls.PowerTrend)moneyWindow.FindName("Trend");
                    Assert.IsTrue(trend.ActualHeight > 220, "图表应填满指标卡下方的空间。");
                    Assert.AreSame(money.HistoryData, trend.History);
                    Assert.AreEqual(period == "今日" ? EnergyPeriod.Day : period == "本月" ? EnergyPeriod.Month : EnergyPeriod.Year, trend.History!.Period);
                    Assert.AreEqual(period == "今日" ? 12 : period == "本月" ? 24 : 10, trend.History.Buckets.Count);
                    Assert.IsNull(trend.History.Source);
                    Assert.IsTrue(trend.History.Buckets.All(b => b.Contributions.Count == 5));
                    Assert.AreEqual(money.ChartRangeLabel, ((System.Windows.Controls.TextBlock)moneyWindow.FindName("ChartRangeLabel")).Text);
                    var bitmap = new RenderTargetBitmap(1104, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
                    var label = new DrawingVisual();
                    using (var draw = label.RenderOpen())
                        draw.DrawText(new FormattedText("界面预览 · 测试数据，非真实测量", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface("Microsoft YaHei UI"), 12, Brushes.Gold, 1), new Point(809, 734));
                    bitmap.Render(label);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
                    var firstRecorded = trend.History.Buckets.First(b => b.Energy.CoveredDuration > TimeSpan.Zero);
                    var domain = PCWatt.App.Controls.PowerTrend.AxisEnd(trend.History, trend.TimeZone) - trend.History.Summary.Start;
                    var center = firstRecorded.Energy.Start + (firstRecorded.Energy.End - firstRecorded.Energy.Start) / 2;
                    trend.SetHoverPosition(new Point(52 + (center - trend.History.Summary.Start).TotalSeconds / domain.TotalSeconds * (trend.ActualWidth - 80), trend.ActualHeight / 2));
                    panel.UpdateLayout();
                    var hover = new RenderTargetBitmap(1104, 760, 96, 96, PixelFormats.Pbgra32); hover.Render(panel); hover.Render(label);
                    var hoverEncoder = new PngBitmapEncoder(); hoverEncoder.Frames.Add(BitmapFrame.Create(hover));
                    using (var hoverFile = File.Create(Path.Combine(directory, name.Replace("stacked-chart", "clear-chart-hover", StringComparison.Ordinal)))) hoverEncoder.Save(hoverFile);
                    trend.SetHoverPosition(null);
                    panel.Measure(new Size(944, 660)); panel.Arrange(new Rect(0, 0, 944, 660)); panel.UpdateLayout();
                    Assert.IsTrue(trend.TranslatePoint(new Point(0, trend.ActualHeight), panel).Y < 630);
                    Assert.IsTrue(trend.ActualHeight > 140);
                    var minimum = new RenderTargetBitmap(944, 660, 96, 96, PixelFormats.Pbgra32); minimum.Render(panel);
                    var stamp = new DrawingVisual();
                    using (var draw = stamp.RenderOpen()) draw.DrawText(new FormattedText("测试数据，非真实测量", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei UI"), 11, Brushes.DarkGoldenrod, 1), new Point(770, 640));
                    minimum.Render(stamp); var small = new PngBitmapEncoder(); small.Frames.Add(BitmapFrame.Create(minimum));
                    using var smallFile = File.Create(Path.Combine(directory, name.Replace("-fixture", "-minimum-fixture", StringComparison.Ordinal))); small.Save(smallFile);
                }
                money.ShowDayBillingCommand.Execute(null);
                money.SelectCategoryCommand.ExecuteAsync("memory").GetAwaiter().GetResult();
                var memoryPanel = (FrameworkElement)moneyWindow.Content;
                memoryPanel.Measure(new Size(1104, 760)); memoryPanel.Arrange(new Rect(0, 0, 1104, 760)); memoryPanel.UpdateLayout();
                var memoryChart = (PCWatt.App.Controls.PowerTrend)moneyWindow.FindName("Trend");
                Assert.IsTrue(memoryChart.History!.Buckets.All(b => b.Contributions.Count == 0));
                Assert.Contains("非内存实时实测", money.CompactPowerHint);
                var memoryBitmap = new RenderTargetBitmap(1104, 760, 96, 96, PixelFormats.Pbgra32); memoryBitmap.Render(memoryPanel);
                var memoryStamp = new DrawingVisual();
                using (var draw = memoryStamp.RenderOpen()) draw.DrawText(new FormattedText("界面预览 · 测试数据，非真实测量", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 12, Brushes.DarkGoldenrod, 1), new Point(809, 734));
                memoryBitmap.Render(memoryStamp);
                var memoryEncoder = new PngBitmapEncoder(); memoryEncoder.Frames.Add(BitmapFrame.Create(memoryBitmap));
                using (var memoryFile = File.Create(Path.Combine(directory, "calm-chart-memory-fixture.png"))) memoryEncoder.Save(memoryFile);
                moneyWindow.Close();
                Assert.IsNull(moneyWindow.Content); Assert.IsNull(moneyWindow.DataContext);
                Assert.AreEqual(0, moneyWindow.Resources.MergedDictionaries.Count);
                Assert.IsNull(money.HistoryData); Assert.IsTrue(money.IsSampling);
                var hardware = HardwareViewModelTests.Create(new HardwareViewModelTests.FakeChannelDashboard());
                hardware.Settings = model.Settings;
                hardware.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                var hardwareWindow = new MainWindow(hardware, 30, 30);
                hardware.UpdatePreferences(false, 30, 30);
                foreach (var (width, height, name) in new[] { (1164, 880, "hardware-dashboard.png"),
                    (944, 660, "hardware-minimum.png"), (1164, 880, "hardware-all-hidden.png"), (1164, 880, "hardware-modes.png") })
                {
                    if (name == "hardware-all-hidden.png")
                    {
                        foreach (var row in hardware.ChannelOptions.ToArray()) row.IsPlotted = false;
                        hardware.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    if (name == "hardware-modes.png") hardware.ShowSettingsCommand.Execute(null);
                    var hardwareContent = (FrameworkElement)hardwareWindow.Content;
                    hardwareContent.Measure(new Size(width, height));
                    hardwareContent.Arrange(new Rect(0, 0, width, height));
                    hardwareContent.UpdateLayout();
                    if (name == "hardware-dashboard.png")
                    {
                        hardware.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                        hardwareContent.UpdateLayout();
                        var selector = (FrameworkElement)hardwareWindow.FindName("CategorySelector");
                        var categories = Descendants<System.Windows.Controls.RadioButton>(selector).ToArray();
                        Assert.HasCount(5, categories);
                        Assert.AreEqual("显卡", categories.Single(c => c.IsChecked == true).Content);
                        Assert.IsFalse(Descendants<System.Windows.Controls.CheckBox>(hardwareContent).Any(c => c.DataContext is PCWatt.App.ViewModels.PowerChannelViewModel));
                        Assert.IsNull(hardwareWindow.FindName("RecordingDetails"));
                    }
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(hardwareContent);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
                }
                hardwareWindow.Close();
                var simpleModel = SimpleSelectionTests.Create();
                simpleModel.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                var simpleWindow = new MainWindow(simpleModel, 30, 30); simpleModel.UpdatePreferences(false, 30, 30);
                foreach (var key in new[] { "whole", "other", "memory" })
                {
                    simpleModel.SelectCategoryCommand.ExecuteAsync(key).GetAwaiter().GetResult();
                    var panel = (FrameworkElement)simpleWindow.Content;
                    panel.Measure(new Size(1104, 760)); panel.Arrange(new Rect(0, 0, 1104, 760)); panel.UpdateLayout();
                    var chart = (FrameworkElement)simpleWindow.FindName("Trend");
                    Assert.IsTrue(chart.TranslatePoint(new Point(0, chart.ActualHeight), panel).Y < 730, "默认窗口中应能看到完整曲线。");
                    var bitmap = new RenderTargetBitmap(1104, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
                    var label = new DrawingVisual();
                    using (var draw = label.RenderOpen())
                        draw.DrawText(new FormattedText("界面预览 · 测试数据，非真实测量", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface("Microsoft YaHei UI"), 12, Brushes.Gold, 1), new Point(810, 734));
                    bitmap.Render(label);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, "simple-" + key + "-fixture.png")); encoder.Save(file);
                }
                simpleWindow.Close();
                var easyModel = HardwareViewModelTests.Create(new HardwareViewModelTests.FakeChannelDashboard(true));
                var easyPreferences = new PCWatt.Infrastructure.Configuration.AppSettings();
                easyModel.Settings = new PCWatt.App.ViewModels.SettingsViewModel(() => easyPreferences,
                    update => { easyPreferences = update(easyPreferences); return Task.CompletedTask; },
                    powerChannels: () => new HardwareViewModelTests.FakeChannelDashboard(true).Channels);
                easyModel.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                easyModel.ShowSettingsCommand.Execute(null);
                var easyWindow = new MainWindow(easyModel, 30, 30); easyModel.UpdatePreferences(false, 30, 30);
                var easyPanel = (FrameworkElement)easyWindow.Content;
                foreach (var advanced in new[] { false, true })
                {
                    easyPanel.Measure(new Size(1164, 880)); easyPanel.Arrange(new Rect(0, 0, 1164, 880)); easyPanel.UpdateLayout();
                    var calibration = Descendants<System.Windows.Controls.Expander>(easyPanel).Single(e => e.Header?.ToString()?.StartsWith("高级：手动校准", StringComparison.Ordinal) == true);
                    Assert.AreEqual(advanced, calibration.IsExpanded);
                    if (!advanced)
                    {
                        calibration.IsExpanded = true;
                        easyPanel.UpdateLayout();
                        // Explicit item template must render a friendly name in the selected box.
                        var picker = Descendants<System.Windows.Controls.ComboBox>(easyPanel).Single(c => c.ItemsSource == easyModel.Settings.EstimateCpuOptions);
                        Assert.IsNotNull(picker.ItemTemplate); Assert.AreEqual(string.Empty, picker.DisplayMemberPath);
                        Assert.IsTrue(Descendants<System.Windows.Controls.TextBlock>(picker).Any(t => t.Text.Contains("AMD Ryzen 7 9700X", StringComparison.Ordinal)));
                        Assert.IsFalse(Descendants<System.Windows.Controls.TextBlock>(picker).Any(t => t.Text.Contains("PowerChannel {", StringComparison.Ordinal)));
                        calibration.IsExpanded = false; easyPanel.UpdateLayout();
                    }
                    else
                    {
                        var scroll = Descendants<System.Windows.Controls.ScrollViewer>(easyPanel).First();
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + calibration.TranslatePoint(new Point(), scroll).Y - 24);
                        easyPanel.UpdateLayout();
                    }
                    var bitmap = new RenderTargetBitmap(1164, 880, 96, 96, PixelFormats.Pbgra32); bitmap.Render(easyPanel);
                    var label = new DrawingVisual();
                    using (var draw = label.RenderOpen())
                        draw.DrawText(new FormattedText("布局测试 · 非真实硬件测量", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface("Microsoft YaHei UI"), 12, Brushes.Gold, 1), new Point(890, 858));
                    bitmap.Render(label);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, advanced ? "settings-advanced-fixture.png" : "settings-easy-fixture.png")); encoder.Save(file);
                    if (!advanced) calibration.IsExpanded = true;
                }
                easyWindow.Close();
                foreach (var (profile, name) in new (WholePcEstimateProfile?, string)[] { (null, "whole-unknown.png"), (WholePcAndCpuTests.Profile, "whole-estimated-fixture.png") })
                {
                    var wholeModel = new PCWatt.App.ViewModels.MainViewModel(new WholePcAndCpuTests.WholeDashboard(profile),
                        new PCWatt.Core.Services.CostCalculator(), null, new FakeClock(TestData.Origin), TimeZoneInfo.Utc,
                        TimeSpan.FromSeconds(90), showWholePc: true);
                    wholeModel.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                    var wholeWindow = new MainWindow(wholeModel, 30, 30); wholeModel.UpdatePreferences(false, 30, 30);
                    var panel = (FrameworkElement)wholeWindow.Content;
                    panel.Measure(new Size(1164, 880)); panel.Arrange(new Rect(0, 0, 1164, 880)); panel.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1164, 880, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
                    var watermark = new DrawingVisual();
                    using (var draw = watermark.RenderOpen())
                    {
                        draw.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 30, 40, 55)), null, new Rect(650, 848, 490, 27));
                        draw.DrawText(new FormattedText("布局测试 · 固定样本，非真实硬件测量", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface("Microsoft YaHei UI"), 12, Brushes.Gold, 1), new Point(670, 854));
                    }
                    bitmap.Render(watermark);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
                    wholeWindow.Close();
                }
                var monitorTime = new ManualSamplingTime();
                var monitorSource = new TrackingPowerSource(monitorTime);
                var monitorRepository = new MemoryEnergyRepository();
                var monitor = new PCWatt.Core.Services.SamplingCoordinator(() => monitorSource, monitorRepository, 5, monitorTime);
                monitor.Start();
                SamplingCoordinatorTests.UntilAsync(() => monitorSource.Reads == 1 && monitorTime.ActiveTimers == 1).GetAwaiter().GetResult();
                var creations = 0;
                var session = new PCWatt.App.Services.OnDemandWindowSession(() =>
                {
                    creations++;
                    var freshModel = SimpleSelectionTests.Create();
                    freshModel.RefreshAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                    return new MainWindow(freshModel, 5, 30, monitor);
                });
                Assert.IsNull(session.Current); Assert.AreEqual(0, creations, "后台不应提前创建窗口。");
                MainWindow? previousWindow = null;
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    var freshWindow = session.Open();
                    Assert.AreSame(freshWindow, session.Open()); Assert.AreNotSame(previousWindow, freshWindow);
                    Assert.AreEqual(1, freshWindow.Resources.MergedDictionaries.Count);
                    Assert.AreEqual(cycle + 1, creations);
                    session.Release(); session.Release(); Assert.IsNull(session.Current);
                    Assert.IsNull(freshWindow.Content); Assert.IsNull(freshWindow.DataContext);
                    Assert.AreEqual(0, freshWindow.Resources.MergedDictionaries.Count);
                    var reads = monitorSource.Reads;
                    monitorTime.Advance(TimeSpan.FromSeconds(5));
                    SamplingCoordinatorTests.UntilAsync(() => monitorSource.Reads > reads && monitorTime.ActiveTimers == 1).GetAwaiter().GetResult();
                    Assert.AreEqual(0, monitorSource.Disposals, "关闭界面不能释放仍在监控的硬件对象。");
                    previousWindow = freshWindow;
                }
                monitor.StopAsync().GetAwaiter().GetResult(); monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Assert.AreEqual(4, monitorRepository.Samples.Length, "按需重建界面后仍只保留一个有效采样循环。");
                Assert.AreEqual(0, application.Windows.Count);
                application.Shutdown();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF 渲染超时。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCWatt.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到解决方案目录。");
    }
}
