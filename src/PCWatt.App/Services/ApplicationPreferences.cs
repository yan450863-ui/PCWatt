using PCWatt.Core.Abstractions;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Windows;

namespace PCWatt.App.Services;

public sealed class ApplicationPreferences(AppSettings current, IUserSettingsStore store,
    AutostartService autostart, ISamplingController sampling)
{
    private readonly SemaphoreSlim _gate = new(1);
    public AppSettings Current { get; private set; } = current;
    public event EventHandler? Changed;

    public Task ApplyAsync(AppSettings settings) => UpdateAsync(_ => settings);

    public async Task UpdateAsync(Func<AppSettings, AppSettings> update)
    {
        await _gate.WaitAsync();
        try
        {
            var previous = Current;
            var settings = update(previous);
            settings.Validate();
            var autostartChanged = settings.AutoStartEnabled != previous.AutoStartEnabled;
            var registration = autostartChanged ? autostart.CurrentCommand : null;
            try
            {
                if (autostartChanged) autostart.SetEnabled(settings.AutoStartEnabled);
                await store.SaveAsync(settings);
            }
            catch
            {
                if (autostartChanged) autostart.Restore(registration);
                throw;
            }
            // Publish only after the file commit. No extra sampling loop is started here.
            Current = settings;
            if (settings.SamplingIntervalSeconds != sampling.IntervalSeconds)
                await sampling.ChangeIntervalAsync(settings.SamplingIntervalSeconds);
            if (settings.RecordingPaused != sampling.IsPaused) await sampling.SetPausedAsync(settings.RecordingPaused);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task WaitForIdleAsync() { await _gate.WaitAsync(); _gate.Release(); }
}
