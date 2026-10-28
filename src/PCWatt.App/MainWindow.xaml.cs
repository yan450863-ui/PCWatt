using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PCWatt.App.ViewModels;
using PCWatt.App.Services;
using PCWatt.Core.Abstractions;

namespace PCWatt.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISamplingController? _sampling;
    private readonly Func<bool>? _closeToTray;
    private readonly Func<Task>? _exit;
    private readonly Action? _releaseView;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _presentationLifetime = new();
    private volatile bool _presenting;
    private readonly SemaphoreSlim _presentationGate = new(1);
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, int samplingIntervalSeconds, int maximumGapSeconds,
        ISamplingController? sampling = null, Func<bool>? closeToTray = null, Func<Task>? exit = null, Action? releaseView = null,
        bool setupPrice = false)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/PCWatt.App;component/Themes/Theme.xaml", UriKind.Absolute) });
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        StateChanged += (_, _) => UpdateWindowChromeState();
        _viewModel = viewModel;
        viewModel.SetQueryLifetime(_presentationLifetime.Token);
        _sampling = sampling;
        _closeToTray = closeToTray;
        _exit = exit;
        _releaseView = releaseView;
        DataContext = viewModel;
        Trend.MaximumGapSeconds = maximumGapSeconds;
        viewModel.UpdatePreferences(viewModel.MotionEnabled, samplingIntervalSeconds, maximumGapSeconds);
        if (sampling is not null) sampling.StateChanged += OnSampleChanged;
        _clockTimer.Tick += (_, _) => _viewModel.TickClock();
        Loaded += async (_, _) => await RefreshAsync();
        if (setupPrice) Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            TariffEditorHeading.BringIntoView(); PriceInputBox.Focus();
        });
        IsVisibleChanged += (_, _) => UpdateActivity();
        StateChanged += (_, _) => UpdateActivity();
        Activated += (_, _) => UpdateActivity();
        Deactivated += (_, _) => UpdateActivity();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _presenting = false;
            _viewModel.SetPresentationActive(false);
            MoneyMotion.Release(); HardwareMotion.Release();
            _lifetime.Cancel();
            _presentationLifetime.Cancel();
            _presentationLifetime.Dispose();
            _lifetime.Dispose();
            if (_sampling is not null) _sampling.StateChanged -= OnSampleChanged;
            DataContext = null;
            Content = null;
            Resources.MergedDictionaries.Clear();
        };
    }

    public void ApplyPreferences(bool motion, int interval, int maximumGap)
    {
        Trend.MaximumGapSeconds = maximumGap;
        _viewModel.UpdatePreferences(motion, interval, maximumGap);
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowChromeState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // Segoe 图标：E922 最大化 / E923 还原。标题栏为自绘，需要手动同步字形。
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private bool Presenting => IsVisible && WindowState != WindowState.Minimized && IsActive;
    private void OnSampleChanged(object? sender, EventArgs e)
    {
        if (!_presenting || !_viewModel.IsOverview || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(async () => { if (Presenting) await RefreshAsync(); });
    }
    private async Task RefreshAsync()
    {
        if (!Presenting || !_viewModel.IsOverview || _lifetime.IsCancellationRequested) return;
        var token = _presentationLifetime.Token;
        try
        {
            await _presentationGate.WaitAsync(token);
            try
            {
                if (!Presenting) return;
                await _viewModel.RefreshAsync(false, token);
                if (_sampling?.LastError is { } error) _viewModel.ReportError(error);
            }
            finally { _presentationGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void UpdateActivity()
    {
        if (_lifetime.IsCancellationRequested) return;
        _presenting = Presenting;
        if (Presenting && _presentationLifetime.IsCancellationRequested)
        {
            _presentationLifetime.Dispose();
            _presentationLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        if (!Presenting) _presentationLifetime.Cancel();
        _viewModel.SetQueryLifetime(_presentationLifetime.Token);
        _viewModel.SetPresentationActive(IsVisible && WindowState != WindowState.Minimized);
        _viewModel.SetForegroundActive(Presenting);
        if (Presenting) { _clockTimer.Start(); _ = RefreshAsync(); }
        else _clockTimer.Stop();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_closeToTray?.Invoke() == true)
        {
            e.Cancel = true;
            if (_releaseView is not null) _ = Dispatcher.BeginInvoke(_releaseView);
            else Hide();
            return;
        }
        if (_exit is not null) { e.Cancel = true; await _exit(); }
    }
    public void CloseForExit() { _allowClose = true; Close(); }
}
