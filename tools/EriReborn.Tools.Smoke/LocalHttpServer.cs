using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Minimal static HTTP/1.1 server used by the smoke run. It exists so the real
/// composition root can be exercised against a genuine HTTP endpoint without
/// depending on an external service.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public void Add(string path, byte[] content) => _routes[path] = content;

    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                {
                    return;
                }

                var header = Encoding.ASCII.GetString(buffer, 0, read);
                var firstLine = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                var parts = firstLine.Split(' ');
                var path = parts.Length > 1 ? parts[1] : "/";

                var body = _routes.TryGetValue(path, out var found) ? found : Array.Empty<byte>();
                var status = _routes.ContainsKey(path) ? "200 OK" : "404 Not Found";

                var head = $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                if (body.Length > 0)
                {
                    await stream.WriteAsync(body);
                }

                await stream.FlushAsync();
            }
        }
        catch
        {
            // A smoke helper must never crash the run.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Already stopped.
        }

        _cts.Dispose();
    }
}
