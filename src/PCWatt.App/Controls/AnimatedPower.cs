using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace PCWatt.App.Controls;

/// <summary>Display frames never enter the repository or energy calculation.</summary>
public sealed class AnimatedPower : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(decimal?), typeof(AnimatedPower), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(nameof(MotionEnabled), typeof(bool), typeof(AnimatedPower), new PropertyMetadata(true, Changed));
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register("DisplayValue", typeof(double), typeof(AnimatedPower), new PropertyMetadata(0d, (d, _) => ((AnimatedPower)d).RenderText()));
    public decimal? Value { get => (decimal?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool MotionEnabled { get => (bool)GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    public AnimatedPower() { Text = "—"; IsVisibleChanged += (_, _) => Animate(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs _) => ((AnimatedPower)d).Animate();
    private void Animate()
    {
        var previous = (double)GetValue(DisplayValueProperty);
        BeginAnimation(DisplayValueProperty, null);
        if (Value is null) { Text = "—"; return; }
        SetValue(DisplayValueProperty, (double)Value.Value);
        if (IsVisible && MotionEnabled && SystemParameters.ClientAreaAnimation)
        {
            var animation = new DoubleAnimation(previous, (double)Value.Value, TimeSpan.FromMilliseconds(650))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(animation, 30);
            BeginAnimation(DisplayValueProperty, animation);
        }
        else RenderText();
    }
    private void RenderText() => Text = Value is null ? "—" : ((double)GetValue(DisplayValueProperty)).ToString("F1", CultureInfo.CurrentCulture);
}
