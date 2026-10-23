namespace BetterVitals.Sensors.Lhm;

/// <summary>Trailing-edge debounce: the callback runs once, <c>delay</c> after the last <see cref="Signal"/>. Thread-safe.</summary>
internal sealed class Debouncer : IDisposable
{
    private readonly Timer _timer;
    private readonly TimeSpan _delay;
    private volatile bool _disposed;

    public Debouncer(TimeSpan delay, Action callback)
    {
        _delay = delay;
        _timer = new Timer(_ =>
        {
            if (_disposed) return;
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Core.Log.Error("lhm: debounced callback failed", ex);
            }
        });
    }

    public void Signal()
    {
        if (_disposed) return;
        try
        {
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // raced with Dispose
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
