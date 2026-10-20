using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Lantern.Core;

// No namespace: the test entry point is top-level statements in the global
// namespace, so an unqualified FakeEngine resolves there.

/// <summary>
/// A stand-in for CS2's console channel, so the suite exercises the real
/// transport instead of a mock of it.
///
/// It reproduces the two halves the launcher depends on:
/// <list type="bullet">
/// <item>commands arrive on a real named pipe, one line each, with
/// semicolon-separated parts executed in order — exactly like a console line;</item>
/// <item>all output appears on the engine's "stdout", fed here through
/// <see cref="Output"/> so <see cref="ConsolePipe.ReadOutput"/> runs for real.</item>
/// </list>
/// The engine's own output pipe is deliberately left unconnected: measurement
/// showed CS2 opens it but never writes to it, and nothing may depend on it.
/// </summary>
sealed class FakeEngine : IAsyncDisposable
{
    readonly ConsolePipe pipe;
    readonly NamedPipeClientStream client;
    readonly LineFeed stdout = new();
    readonly Task reader;

    public string CommandPath => pipe.CommandPath;
    public ConsolePipe Pipe => pipe;
    public string Session { get; }
    /// <summary>Raw command lines as received, before any semicolon splitting.</summary>
    public ConcurrentQueue<string> RawCommands { get; } = new();
    /// <summary>Stream standing in for CS2's stdout.</summary>
    public Stream Output => stdout;

    public FakeEngine(Func<string, string[]> execute)
    {
        Session = "test_" + Guid.NewGuid().ToString("N")[..12];
        pipe = new ConsolePipe(Session);
        // The launcher owns the server side of the command pipe (it writes), so
        // the engine side is the client that reads. A direction mismatch surfaces
        // as UnauthorizedAccessException rather than a helpful error.
        client = new NamedPipeClientStream(".", "Lantern-" + Session + "-cmd", PipeDirection.In);
        client.Connect(2000);
        reader = Task.Run(() => Read(execute));
    }

    void Read(Func<string, string[]> execute)
    {
        using var reader_ = new StreamReader(client, Encoding.UTF8);
        try
        {
            while (reader_.ReadLine() is string line)
            {
                RawCommands.Enqueue(line);
                foreach (var part in line.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
                {
                    // Echo is how the launcher proves the channel is alive and how it
                    // delimits captured output, so it must behave like the real one.
                    if (part.StartsWith("echo ", StringComparison.Ordinal)) stdout.WriteLine(part[5..]);
                    else foreach (var produced in execute(part)) stdout.WriteLine(produced);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { pipe.Dispose(); } catch (Exception) { }
        try { await Task.WhenAny(reader, Task.Delay(1000)); } catch (Exception) { }
        stdout.Dispose();
        client.Dispose();
    }
}

/// <summary>
/// An append-only byte stream whose reads block until data arrives. That is what
/// the engine's stdout looks like to the launcher: bytes appear only when the
/// engine produces them, and the reader must never spin on a premature zero.
/// </summary>
sealed class LineFeed : Stream
{
    readonly BlockingCollection<byte[]> chunks = new();
    byte[]? current;
    int offset;

    public void WriteLine(string line) => chunks.Add(Encoding.UTF8.GetBytes(line + "\n"));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int bufferOffset, int count)
    {
        while (current == null || offset >= current.Length)
        {
            if (!chunks.TryTake(out current, Timeout.Infinite)) return 0;
            offset = 0;
        }
        var take = Math.Min(count, current.Length - offset);
        Array.Copy(current, offset, buffer, bufferOffset, take);
        offset += take;
        return take;
    }

    public override Task<int> ReadAsync(byte[] buffer, int bufferOffset, int count, CancellationToken token)
        => Task.Run(() => Read(buffer, bufferOffset, count), token);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
            return new(ReadAsync(segment.Array!, segment.Offset, segment.Count, token));
        // Not array-backed: read into scratch and copy back, so no byte is lost.
        var scratch = new byte[buffer.Length];
        var count = Read(scratch, 0, scratch.Length);
        scratch.AsSpan(0, count).CopyTo(buffer.Span);
        return new(count);
    }

    public override void Flush() { }
    public override long Seek(long seekOffset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) chunks.CompleteAdding();
        base.Dispose(disposing);
    }
}
