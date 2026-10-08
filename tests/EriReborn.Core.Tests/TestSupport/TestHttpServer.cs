using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EriReborn.Core.Tests.TestSupport;

/// <summary>One parsed request. RangeEnd matters: a middle segment is not the tail.</summary>
public sealed record TestRequest(string Path, long? RangeStart, long? RangeEnd = null)
{
    /// <summary>The Cookie header, so a session round-trip can be observed.</summary>
    public string? CookieHeader { get; init; }

    public bool HasRange => RangeStart is not null;

    public override string ToString()
        => HasRange ? $"{Path} bytes={RangeStart}-{RangeEnd?.ToString() ?? string.Empty}" : Path;
}

public sealed record TestResponse(int StatusCode, byte[] Body, string ContentType = "application/octet-stream")
{
    /// <summary>Additional response headers, for cases like Retry-After.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>
    /// The byte range this body actually represents. Without it a 206's
    /// Content-Range is guessed, and a guessed one hides a wrong slice.
    /// </summary>
    public long? RangeStart { get; init; }

    public long? RangeEnd { get; init; }

    public long? TotalLength { get; init; }

    /// <summary>Waits before answering, so cancellation and timeouts can be exercised.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Closes the socket without answering, so a transport failure is a real one.</summary>
    public bool DropConnection { get; init; }

    public static TestResponse Ok(byte[] body) => new(200, body);

    public static TestResponse Partial(byte[] body) => new(206, body);

    public static TestResponse NotFound() => new(404, Array.Empty<byte>());
}

/// <summary>
/// A scriptable HTTP/1.1 server on a real socket.
///
/// <para>
/// It answers over TCP rather than through a message handler on purpose: range
/// requests, ignored ranges, Retry-After, dropped connections and cancellation
/// only behave realistically when there is a socket involved. A handler would
/// prove the code calls the right APIs; this proves it copes with a server that
/// answers badly (spec 24 / milestone 1).
/// </para>
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<TestRequest, TestResponse> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentQueue<TestRequest> _requests = new();

    public TestHttpServer(Func<TestRequest, TestResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    public int RequestCount;

    /// <summary>Every request seen, in order, safe to read while the server runs.</summary>
    public IReadOnlyList<TestRequest> Requests => _requests.ToArray();

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
                var headerText = await ReadHeadersAsync(stream);
                if (string.IsNullOrEmpty(headerText))
                {
                    return;
                }

                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var requestLine = lines[0].Split(' ');
                var path = requestLine.Length > 1 ? requestLine[1] : "/";

                long? rangeStart = null;
                long? rangeEnd = null;
                string? cookieHeader = null;

                foreach (var line in lines.Skip(1))
                {
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                    {
                        cookieHeader = line["Cookie:".Length..].Trim();
                        continue;
                    }

                    if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = line["Range:".Length..].Trim();
                    if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var spec = value["bytes=".Length..].Trim();
                    var dash = spec.IndexOf('-');
                    if (dash < 0)
                    {
                        continue;
                    }

                    // Parse both ends: "bytes=1024-2047" is a middle segment, and
                    // treating it as an open-ended tail is exactly the bug this
                    // server exists to catch.
                    if (long.TryParse(spec[..dash], out var from))
                    {
                        rangeStart = from;
                    }

                    if (long.TryParse(spec[(dash + 1)..], out var to))
                    {
                        rangeEnd = to;
                    }
                }

                var request = new TestRequest(path, rangeStart, rangeEnd) { CookieHeader = cookieHeader };
                _requests.Enqueue(request);
                Interlocked.Increment(ref RequestCount);

                var response = _handler(request);

                if (response.DropConnection)
                {
                    client.Client.Close();
                    return;
                }

                if (response.Delay is { } delay && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }

                await WriteResponseAsync(stream, response).ConfigureAwait(false);
            }
        }
        catch
        {
            // A test server must never crash the test run.
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static async Task WriteResponseAsync(NetworkStream stream, TestResponse response)
    {
        var headers = new StringBuilder();
        headers.Append($"HTTP/1.1 {response.StatusCode} {Reason(response.StatusCode)}\r\n");
        headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append($"Content-Type: {response.ContentType}\r\n");

        if (response.StatusCode == 206 && response.RangeStart is { } start)
        {
            // Use the range the body really covers. Computing it from the body
            // length assumes every 206 runs to the end of the file.
            var end = response.RangeEnd ?? (start + response.Body.Length - 1);
            var total = response.TotalLength ?? (end + 1);
            headers.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
        }

        if (response.ExtraHeaders is not null)
        {
            foreach (var (name, value) in response.ExtraHeaders)
            {
                headers.Append($"{name}: {value}\r\n");
            }
        }

        headers.Append($"Content-Length: {response.Body.Length}\r\n");
        headers.Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
        await stream.WriteAsync(headerBytes);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body);
        }

        await stream.FlushAsync();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        408 => "Request Timeout",
        410 => "Gone",
        416 => "Range Not Satisfiable",
        425 => "Too Early",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Status",
    };

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
