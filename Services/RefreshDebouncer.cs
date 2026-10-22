using System;
using System.Threading;

namespace Ocula.Services;

/// <summary>
/// Turns a burst of "something changed" calls into one action: it runs <paramref name="action"/> once the calls have been quiet for
/// <paramref name="quiet"/>, but never later than <paramref name="maxWait"/> after the first call of the burst (so a long, steady run of
/// changes, such as a face scan, still produces updates now and then).
/// </summary>
public sealed class RefreshDebouncer : IDisposable
{
    private readonly Action _action;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _maxWait;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private DateTime _first = DateTime.MinValue;

    public RefreshDebouncer(Action action, TimeSpan quiet, TimeSpan maxWait)
    {
        _action = action;
        _quiet = quiet;
        _maxWait = maxWait;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Notes a change. Cheap; safe to call from any thread, as often as you like.</summary>
    public void Request()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_first == DateTime.MinValue) _first = now;
            var left = _maxWait - (now - _first);
            _timer.Change(left <= TimeSpan.Zero ? TimeSpan.Zero : (_quiet < left ? _quiet : left), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Forgets a waiting request (the caller is about to do the work, or undo it, itself).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _first = DateTime.MinValue;
        }
    }

    private void Fire()
    {
        lock (_gate) _first = DateTime.MinValue;
        _action();
    }

    public void Dispose() => _timer.Dispose();
}
