using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace DayZModManager.Services;

internal sealed class RptLogTail : IDisposable
{
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private FileStream? _fs;
    private StreamReader? _reader;
    private string? _currentPath;
    private long _offset;
    private string _directory = string.Empty;
    private string[] _patterns = Array.Empty<string>();
    private bool _disposed;
    private int _pumpRunning; // re-entrancy guard; accessed via Interlocked

    public event Action<string>? LineAppended;
    public event Action<string>? ActiveFileChanged;

    public void Start(string directory, string[] patterns)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RptLogTail));
        Stop();

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        lock (_gate)
        {
            _directory = directory;
            _patterns = patterns.Length == 0 ? new[] { "*.RPT" } : patterns;

            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Renamed += OnFsEvent;

            _debounce = new Timer(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
            // Initial pump so a pre-existing log file is picked up.
            ScheduleDebounce();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _watcher?.Dispose(); } catch { }
            try { _debounce?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _fs?.Dispose(); } catch { }
            _watcher = null;
            _debounce = null;
            _reader = null;
            _fs = null;
            _currentPath = null;
            _offset = 0;
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => ScheduleDebounce();

    private void ScheduleDebounce()
    {
        lock (_gate)
        {
            _debounce?.Change(250, Timeout.Infinite);
        }
    }

    private void Pump()
    {
        // Prevent concurrent or re-entrant executions (timer can fire while Pump is still running).
        if (Interlocked.CompareExchange(ref _pumpRunning, 1, 0) != 0)
            return;

        try
        {
            // Capture directory/patterns under the lock, then release before hitting the filesystem.
            string directory;
            string[] patterns;
            lock (_gate)
            {
                if (_disposed || string.IsNullOrWhiteSpace(_directory))
                    return;
                directory = _directory;
                patterns  = _patterns;
            }

            string? newest;
            try { newest = FindNewest(directory, patterns); }
            catch { return; }

            if (newest == null) return;

            // Switch active file if it changed.
            if (!string.Equals(newest, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    try { _reader?.Dispose(); } catch { }
                    try { _fs?.Dispose(); } catch { }
                    _reader = null;
                    _fs = null;
                    _offset = 0;
                    _currentPath = newest;
                }
                ActiveFileChanged?.Invoke(newest);
            }

            EnsureOpen();
            if (_reader == null || _fs == null) return;

            // Drain any new content since last pump.
            string? line;
            while ((line = _reader.ReadLine()) != null)
            {
                _offset = _fs.Position;
                LineAppended?.Invoke(line);
            }
        }
        catch
        {
            // Swallow - rotation, transient sharing violations, etc. We'll retry on next FSW event.
        }
        finally
        {
            Interlocked.Exchange(ref _pumpRunning, 0);
        }
    }

    private void EnsureOpen()
    {
        if (_reader != null && _fs != null) return;
        if (_currentPath == null) return;

        try
        {
            _fs = new FileStream(
                _currentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            _fs.Seek(_offset, SeekOrigin.Begin);
            _reader = new StreamReader(_fs, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        }
        catch
        {
            _reader = null;
            try { _fs?.Dispose(); } catch { }
            _fs = null;
        }
    }

    private static string? FindNewest(string directory, string[] patterns)
    {
        try
        {
            var matches = patterns
                .SelectMany(p => Directory.EnumerateFiles(directory, p, SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new FileInfo(p))
                .Where(fi => fi.Exists)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();
            return matches.FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
