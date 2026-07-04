using System;
using System.Threading;

namespace DayZModManager.Services;

/// <summary>
/// Polls once every 30s and raises <see cref="RestartDue"/>/<see cref="UpdateCheckDue"/> when a
/// configured daily restart time or update-check interval has elapsed. This only exists while
/// the manager's GUI process is running - it is not an OS-level cron and will not fire if the
/// app is closed. Callbacks fire on a thread-pool thread; subscribers must marshal to the UI
/// thread themselves (same convention as <see cref="ServerProcessController"/>'s events).
/// </summary>
internal sealed class ServerScheduleService : IDisposable
{
    private readonly Timer _timer;
    private DateTime? _lastRestartFiredLocalDate;
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    private bool _disposed;

    public bool RestartEnabled { get; set; }
    public TimeSpan RestartTimeOfDay { get; set; }
    public bool UpdateCheckEnabled { get; set; }
    public double UpdateCheckIntervalHours { get; set; } = 6;

    public event Action? RestartDue;
    public event Action? UpdateCheckDue;

    public ServerScheduleService()
    {
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void OnTick(object? state)
    {
        try
        {
            var now = DateTime.Now;

            if (RestartEnabled)
            {
                var today = now.Date;
                var due = today + RestartTimeOfDay;
                if (now >= due && _lastRestartFiredLocalDate != today)
                {
                    _lastRestartFiredLocalDate = today;
                    RestartDue?.Invoke();
                }
            }

            if (UpdateCheckEnabled)
            {
                var nowUtc = DateTime.UtcNow;
                var interval = TimeSpan.FromHours(Math.Max(0.5, UpdateCheckIntervalHours));
                if (nowUtc - _lastUpdateCheckUtc >= interval)
                {
                    _lastUpdateCheckUtc = nowUtc;
                    UpdateCheckDue?.Invoke();
                }
            }
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
