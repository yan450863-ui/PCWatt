using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace PCWatt.App.Controls;

public sealed class SvgMotion : FrameworkElement
{
    public static readonly DependencyProperty AssetProperty = DependencyProperty.Register(nameof(Asset), typeof(string), typeof(SvgMotion),
        new FrameworkPropertyMetadata("whole", FrameworkPropertyMetadataOptions.AffectsRender, Reload));
    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(nameof(Playing), typeof(bool), typeof(SvgMotion), new PropertyMetadata(false, Changed));
    public string Asset { get => (string)GetValue(AssetProperty); set => SetValue(AssetProperty, value); }
    public bool Playing { get => (bool)GetValue(PlayingProperty); set => SetValue(PlayingProperty, value); }
    private SvgMotionScene? _scene;
    internal bool IsAnimating => _scene?.IsRunning == true;
    internal void Release()
    {
        SystemParameters.StaticPropertyChanged -= SystemChanged;
        _scene?.SetPlaying(false); _scene = null;
    }
    public SvgMotion()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += SystemChanged; UpdateMotion(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= SystemChanged; _scene?.SetPlaying(false); };
        IsVisibleChanged += (_, _) => UpdateMotion();
    }
    private void SystemChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateMotion(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SvgMotion)d).UpdateMotion();
    private static void Reload(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SvgMotion)d; control._scene?.SetPlaying(false); control._scene = null; control.UpdateMotion();
    }
    private void UpdateMotion() => _scene?.SetPlaying(IsLoaded && IsVisible && Playing && SystemParameters.ClientAreaAnimation);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _scene ??= SvgMotionScene.Load(Asset);
        UpdateMotion();
        var size = Math.Min(ActualWidth, ActualHeight);
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 96, size / 96));
        dc.DrawDrawing(_scene.Drawing);
        dc.Pop(); dc.Pop();
    }
}
