using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PSX.DshProbe;

internal sealed partial class MockDshServer
{
    private byte[]? _zipBytes;

    private string Html => $$"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>DSH Probe Mock</title></head>
        <body>
          <h1 id="title">dsh-mock</h1>
          <input id="probe-input" type="text" value="">
          <form id="probe-form" method="get" action="/form-target">
            <input name="q" value="x"><button id="probe-form-btn" type="submit">go</button>
          </form>
          <a id="export-link" href="/api/session.export" download="session.zip">export</a>
          <a id="external-link" href="https://example.com/probe" target="_blank">external</a>
          <a id="oddport-link" href="http://127.0.0.1:59999/probe" target="_blank">odd port</a>
          <button id="popup-btn" type="button">popup</button>
          <div id="ws-result">ws-idle</div>
          <script>
            window.__probeWsResult = 'idle';
            document.getElementById('popup-btn').addEventListener('click', function () {
              window.open('https://example.com/probe', '_blank');
            });
            function probeWs() {
              return new Promise(function (resolve, reject) {
                try {
                  var ws = new WebSocket('ws://{{BaseUri.Authority}}/ws');
                  var timer = setTimeout(function () { try { ws.close(); } catch (e) {} reject(new Error('ws timeout')); }, 5000);
                  ws.onopen = function () { ws.send('ping'); };
                  ws.onmessage = function (ev) { clearTimeout(timer); resolve(String(ev.data)); try { ws.close(); } catch (e) {} };
                  ws.onerror = function () { clearTimeout(timer); reject(new Error('ws error')); };
                } catch (err) { reject(err); }
              });
            }
          </script>
        </body>
        </html>
        """;

    private byte[] ZipBytes
    {
        get
        {
            if (_zipBytes != null) return _zipBytes;
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("session.txt");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("probe-session-content");
            }
            _zipBytes = buffer.ToArray();
            return _zipBytes;
        }
    }

    private async Task ServeWebSocketAsync(NetworkStream stream, string key, CancellationToken token)
    {
        const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + magic)));
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
            + accept + "\r\n\r\n");
        await stream.WriteAsync(head, token).ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            var b0 = await ReadByteAsync(stream, token).ConfigureAwait(false);
            if (b0 < 0) break;
            var b1 = await ReadByteAsync(stream, token).ConfigureAwait(false);
            if (b1 < 0) break;
            var opcode = b0 & 0x0F;
            var masked = (b1 & 0x80) != 0;
            long length = b1 & 0x7F;
            if (length == 126)
            {
                length = ((long)await ReadByteAsync(stream, token) << 8) | (byte)await ReadByteAsync(stream, token);
            }
            else if (length == 127)
            {
                length = 0;
                for (var i = 0; i < 8; i++)
                    length = (length << 8) | (byte)await ReadByteAsync(stream, token);
            }
            if (length > 65536) break;
            var mask = new byte[4];
            if (masked)
            {
                for (var i = 0; i < 4; i++)
                    mask[i] = (byte)await ReadByteAsync(stream, token);
            }

            var payload = new byte[length];
            var readTotal = 0;
            while (readTotal < length)
            {
                var read = await stream.ReadAsync(
                    payload.AsMemory(readTotal, payload.Length - readTotal), token).ConfigureAwait(false);
                if (read == 0) break;
                readTotal += read;
            }
            if (masked)
            {
                for (var i = 0; i < readTotal; i++)
                    payload[i] ^= mask[i % 4];
            }

            if (opcode == 0x8) break;
            Interlocked.Increment(ref WebSocketMessageCount);
            var reply = Encoding.UTF8.GetBytes($"echo:{Encoding.UTF8.GetString(payload, 0, readTotal)}");
            var frame = new byte[2 + reply.Length];
            frame[0] = 0x81;
            frame[1] = (byte)reply.Length;
            reply.CopyTo(frame, 2);
            await stream.WriteAsync(frame, token).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadByteAsync(NetworkStream stream, CancellationToken token)
    {
        var one = new byte[1];
        var read = await stream.ReadAsync(one, token).ConfigureAwait(false);
        return read == 0 ? -1 : one[0];
    }
}
