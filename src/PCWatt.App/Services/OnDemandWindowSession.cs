namespace PCWatt.App.Services;

/// <summary>Monitoring owns this small factory, never a hidden visual tree.</summary>
internal sealed class OnDemandWindowSession(Func<MainWindow> factory)
{
    public MainWindow? Current { get; private set; }
    public MainWindow Open()
    {
        if (Current is not null) return Current;
        var window = factory();
        Current = window;
        window.Closed += (_, _) => { if (ReferenceEquals(Current, window)) Current = null; };
        return window;
    }
    public void Release()
    {
        var window = Current;
        Current = null;
        window?.CloseForExit();
    }
}
