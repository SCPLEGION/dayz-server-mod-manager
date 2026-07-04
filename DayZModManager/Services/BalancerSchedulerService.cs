using System;
using System.Threading;

namespace DayZModManager.Services;

/// <summary>
/// Polls once every 30s and raises <see cref="RunDue"/> when the configured interval has
/// elapsed since the last run. Only exists while the manager's GUI process is running - it is
/// not an OS-level cron. Callbacks fire on a thread-pool thread; subscribers must marshal to
/// the UI thread themselves (same convention as <see cref="ServerScheduleService"/>).
/// </summary>
internal sealed class BalancerSchedulerService : IDisposable
{
    private readonly Timer _timer;
    private DateTime _lastRunUtc = DateTime.MinValue;
    private bool _disposed;

    public bool Enabled { get; set; }
    public double IntervalMinutes { get; set; } = 60;

    /// <summary>Null when disabled. Otherwise when the next run is due (may already be in the past).</summary>
    public DateTime? NextRunUtc => Enabled
        ? _lastRunUtc + TimeSpan.FromMinutes(Math.Max(5, IntervalMinutes))
        : (DateTime?)null;

    public event Action? RunDue;

    public BalancerSchedulerService()
    {
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void OnTick(object? state)
    {
        try
        {
            if (!Enabled) return;
            var interval = TimeSpan.FromMinutes(Math.Max(5, IntervalMinutes));
            if (DateTime.UtcNow - _lastRunUtc < interval) return;

            _lastRunUtc = DateTime.UtcNow;
            RunDue?.Invoke();
        }
        catch
        {
            // Never let a timer callback exception take down the process.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
