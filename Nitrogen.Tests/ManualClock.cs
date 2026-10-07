namespace Nitrogen.Tests;

/// <summary>A clock a test moves by hand: a set local time in a fixed-offset zone, and timers that fire when it passes them.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    readonly List<ManualTimer> _timers = [];
    readonly TimeZoneInfo _zone = TimeZoneInfo.CreateCustomTimeZone("Manual", start.Offset, "Manual", "Manual");
    DateTimeOffset _now = start.ToUniversalTime();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_timers) return _now;
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    /// <summary>Moves the clock on and fires, outside the lock, every timer it passed.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_timers)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>A one-shot timer (the period is ignored: <see cref="Task.Delay(TimeSpan, TimeProvider)"/> uses none).</summary>
    sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                if (dueTime > TimeSpan.Zero)
                {
                    Due = clock._now + dueTime;
                    clock._timers.Add(this);
                    return true;
                }
            }
            Fire();
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._timers) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
