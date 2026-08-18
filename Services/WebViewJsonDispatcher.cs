using System.Windows.Threading;

namespace PSX.Services;

internal interface IWebViewUiDispatcher
{
    bool CheckAccess();
    bool HasShutdownStarted { get; }
    bool HasShutdownFinished { get; }
    Task InvokeAsync(Action callback);
}

internal sealed class WpfWebViewUiDispatcher(Dispatcher dispatcher) : IWebViewUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public bool HasShutdownStarted => dispatcher.HasShutdownStarted;

    public bool HasShutdownFinished => dispatcher.HasShutdownFinished;

    public Task InvokeAsync(Action callback) => dispatcher.InvokeAsync(callback).Task;
}

/// <summary>
/// Serializes WebView posting onto its owning Dispatcher while making queued
/// work lifecycle-aware. Once Dispose returns, pending callbacks observe the
/// cleared sink and do not post into a closing WebView.
/// </summary>
internal sealed class WebViewJsonDispatcher : IDisposable
{
    private readonly object _sync = new();
    private readonly IWebViewUiDispatcher _dispatcher;
    private Action<string>? _postJson;

    public WebViewJsonDispatcher(Dispatcher dispatcher, Action<string> postJson)
        : this(new WpfWebViewUiDispatcher(dispatcher), postJson)
    {
    }

    internal WebViewJsonDispatcher(IWebViewUiDispatcher dispatcher, Action<string> postJson)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _postJson = postJson ?? throw new ArgumentNullException(nameof(postJson));
    }

    public Task SendAsync(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        lock (_sync)
        {
            if (_postJson == null)
                return Task.CompletedTask;
        }

        if (_dispatcher.CheckAccess())
        {
            PostIfActive(json);
            return Task.CompletedTask;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return Task.CompletedTask;

        return _dispatcher.InvokeAsync(() => PostIfActive(json));
    }

    private void PostIfActive(string json)
    {
        lock (_sync)
        {
            _postJson?.Invoke(json);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _postJson = null;
        }
    }
}
