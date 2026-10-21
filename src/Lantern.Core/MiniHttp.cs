using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lantern.Core;

public sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Param(string name) => Query.TryGetValue(name, out var value) ? value : null;
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    public string Text => Encoding.UTF8.GetString(Body);
}

public sealed record HttpResponse(int Status, string ContentType, byte[] Body, IReadOnlyList<(string Name, string Value)>? Extra = null)
{
    public static HttpResponse Html(string text) => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(text));
    public static HttpResponse Json(string json) => new(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
    public static HttpResponse Text(string text, int status = 200) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
    public static HttpResponse Bytes(byte[] body, string type) => new(200, type, body);
    public HttpResponse With(string name, string value) => this with { Extra = (Extra ?? Array.Empty<(string, string)>()).Append((name, value)).ToArray() };
}

/// <summary>
/// A deliberately small HTTP/1.1 server: one request per connection, no
/// chunked bodies, no compression.
///
/// Why not <see cref="HttpListener"/>: it needs a URL ACL reservation to bind
/// anything other than localhost, which means an elevated first run. A raw
/// <see cref="TcpListener"/> binds a LAN port as the ordinary user, and this
/// console only ever serves one page, a handful of JSON routes and an event
/// stream — nothing that justifies a full framework server.
/// </summary>
public sealed class MiniHttp : IDisposable
{
    readonly TcpListener listener;
    readonly Func<HttpRequest, Task<HttpResponse>> handler;
    readonly CancellationTokenSource lifetime = new();
    long requests;
    string? lastError;

    public int Port { get; }
    public long Requests => Interlocked.Read(ref requests);
    public string? LastError { get { lock (this) return lastError; } }

    public MiniHttp(int port, Func<HttpRequest, Task<HttpResponse>> handler)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "网页端口需为 1024–65535。");
        this.handler = handler;
        Port = port;
        listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        try { listener.Start(); }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new InvalidOperationException($"网页端口 {Port} 无法监听（{(ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse ? "已被其它程序占用，请换一个端口" : ex.Message)}）。", ex);
        }
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { Record(ex.Message); continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                // A browser that opens a socket and says nothing must not pin a task.
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var stream = client.GetStream();
                var request = await Read(stream, timeout.Token);
                if (request == null) return;
                Interlocked.Increment(ref requests);
                HttpResponse response;
                try { response = await handler(request); }
                catch (Exception ex) { Record(ex.Message); response = HttpResponse.Text("内部错误：" + ex.Message, 500); }
                await Write(stream, response, timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    void Record(string message) { lock (this) lastError = message; }

    static async Task Write(NetworkStream stream, HttpResponse response, CancellationToken token)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n");
        head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        // JSON state must never be cached; a cover image may be, and the browser
        // asking for the same thumbnail on every poll is pure waste.
        if (response.Extra == null || !response.Extra.Any(e => e.Name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase)))
            head.Append("Cache-Control: no-store\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("Referrer-Policy: no-referrer\r\n");
        if (response.Extra != null)
            foreach (var (name, value) in response.Extra) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(bytes, token);
        await stream.WriteAsync(response.Body, token);
        await stream.FlushAsync(token);
    }

    static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 500 => "Internal Server Error",
        _ => "Status",
    };

    static async Task<HttpRequest?> Read(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var head = new MemoryStream();
        int terminator = -1;
        while (terminator < 0)
        {
            if (head.Length > 16384) return null;
            int read = await stream.ReadAsync(buffer, token);
            if (read <= 0) return null;
            head.Write(buffer, 0, read);
            var span = head.GetBuffer().AsSpan(0, (int)head.Length);
            terminator = span.IndexOf("\r\n\r\n"u8);
        }
        var raw = head.GetBuffer().AsSpan(0, (int)head.Length).ToArray();
        var headerText = Encoding.UTF8.GetString(raw, 0, terminator);
        var rest = raw.AsSpan(terminator + 4).ToArray();
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0 || !TryRequestLine(lines[0], out var method, out var path, out var query)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("Content-Length", out var declared) && int.TryParse(declared, out var parsed) ? parsed : 0;
        if (length is < 0 or > 65536) return null;
        var body = new byte[length];
        int copied = Math.Min(rest.Length, length);
        rest.AsSpan(0, copied).CopyTo(body);
        while (copied < length)
        {
            int read = await stream.ReadAsync(body.AsMemory(copied, length - copied), token);
            if (read <= 0) break;
            copied += read;
        }
        return new HttpRequest(method, path, ParseQuery(query), headers, body);
    }

    /// <summary>Request line of the form <c>GET /path?a=1 HTTP/1.1</c>.</summary>
    public static bool TryRequestLine(string line, out string method, out string path, out string query)
    {
        method = path = query = "";
        var parts = line.Split(' ');
        if (parts.Length < 2 || parts[0].Length == 0 || !parts[0].All(char.IsLetter)) return false;
        method = parts[0].ToUpperInvariant();
        var target = parts[1];
        int mark = target.IndexOf('?');
        path = mark < 0 ? target : target[..mark];
        query = mark < 0 ? "" : target[(mark + 1)..];
        if (!path.StartsWith('/')) return false;
        return true;
    }

    public static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? "" : pair[(equals + 1)..];
            if (key.Length == 0) continue;
            try { result[Decode(key)] = Decode(value); } catch (UriFormatException) { }
        }
        return result;
    }

    static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    public void Dispose()
    {
        if (lifetime.IsCancellationRequested) return;
        lifetime.Cancel();
        try { listener.Stop(); } catch { }
        lifetime.Dispose();
    }
}
