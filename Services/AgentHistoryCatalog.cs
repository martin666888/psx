namespace PSX.Services;

public interface IAgentHistoryCatalog : IDisposable
{
    event EventHandler? Invalidated;
    void Invalidate();
}

public sealed class AgentHistoryCatalog : IAgentHistoryCatalog
{
    private readonly object _sync = new();
    private Timer? _timer;
    private bool _disposed;

    public event EventHandler? Invalidated;

    public void Invalidate()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _timer ??= new Timer(OnTimer);
            _timer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(object? state) => Invalidated?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
