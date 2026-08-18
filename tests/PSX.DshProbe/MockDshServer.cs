using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PSX.DshProbe;

/// <summary>
/// Loopback stand-in for `dsh web` used by the P0 WebView2 probe. Serves a
/// tiny app surface (input, links, ZIP export, WebSocket echo) over a raw
/// TcpListener so no HttpListener URL-ACL reservation is required. The real
/// DeepSeek Harness runtime is exercised separately by the launch probe.
/// </summary>
internal sealed partial class MockDshServer : IDisposable
{
    private TcpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();

    public Uri BaseUri { get; }
    public int RequestCount;
    public int DownloadCount;

    private MockDshServer(int port) => BaseUri = new Uri($"http://127.0.0.1:{port}/");

    public static MockDshServer Start()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new MockDshServer(port);
        server._listener = new TcpListener(IPAddress.Loopback, port);
        server._listener.Start();
        _ = Task.Run(server.ServeAsync);
        return server;
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                Interlocked.Increment(ref RequestCount);
                var stream = client.GetStream();
                var head = await ReadRequestHeadAsync(stream, token).ConfigureAwait(false);
                if (head == null) return;
                var requestLine = head.Split("\r\n")[0].Split(' ');
                if (requestLine.Length < 2) return;
                var method = requestLine[0];
                var path = requestLine[1].Split('?')[0];
                var headers = ParseHeaders(head);

                if (method == "GET" && path == "/ws"
                    && headers.TryGetValue("Sec-WebSocket-Key", out var wsKey))
                {
                    await ServeWebSocketAsync(stream, wsKey, token).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/")
                {
                    await WriteResponseAsync(stream, "200 OK",
                        Encoding.UTF8.GetBytes(Html), "text/html; charset=utf-8", "", token).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/api/session.export")
                {
                    Interlocked.Increment(ref DownloadCount);
                    await WriteResponseAsync(stream, "200 OK", ZipBytes, "application/zip",
                        "Content-Disposition: attachment; filename=\"session.zip\"", token).ConfigureAwait(false);
                    return;
                }
                await WriteResponseAsync(stream, "404 Not Found",
                    Encoding.UTF8.GetBytes("not found"), "text/plain", "", token).ConfigureAwait(false);
            }
            catch
            {
                // best-effort probe server
            }
        }
    }

    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[1024];
        var builder = new StringBuilder();
        while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return null;
            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.Length > 16 * 1024) return null;
        }
        return builder.ToString();
    }

    private static Dictionary<string, string> ParseHeaders(string head)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, string status, byte[] body, string contentType, string extraHeaders, CancellationToken token)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n{extraHeaders}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
