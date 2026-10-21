using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Lantern.Core;

/// <summary>
/// Transport for CS2's <c>-concommandpipe</c> switch: commands go out through a
/// named pipe, output comes back on the process's stdout.
///
/// Why this shape (all measured on 1.41.9.1):
/// <list type="bullet">
/// <item>CS2 is the pipe *client*. It opens both paths at launch, so the server
/// side must already exist; otherwise the engine asserts in
/// <c>CConsoleCommandPipes::Create</c> and writes a minidump.</item>
/// <item>Paths must be full form (<c>\\.\pipe\NAME</c>). <c>.\pipe\NAME</c> and
/// bare names fail the same assertion.</item>
/// <item>The output pipe CS2 is given stays empty: command output is never
/// redirected into it. Output lands on the process stdout, which is why the
/// launcher must start cs2 itself with a redirected stdout.</item>
/// <item>stdout lines carry no timestamp prefix, so they match the historical
/// console protocol without rewriting any parser.</item>
/// </list>
///
/// The pipe is created through <see cref="NamedPipeServerStream"/> rather than
/// raw Win32 calls: a pending <c>ConnectNamedPipe</c> cannot be cancelled, and
/// closing such a handle deadlocks. The managed async API has no such trap.
/// </summary>
public sealed class ConsolePipe : IDisposable
{
    // One live channel per pipe path lets the terminal window attach by path
    // instead of holding a reference to whoever launched the server.
    static readonly ConcurrentDictionary<string, ConsolePipe> live = new(StringComparer.OrdinalIgnoreCase);

    readonly NamedPipeServerStream command;
    readonly NamedPipeServerStream output;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim writer = new(1, 1);
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int disposed;

    public string CommandPath { get; }
    public string OutputPath { get; }
    public bool IsConnected => connected.Task.IsCompletedSuccessfully;

    /// <summary>Raised for every stdout line worth showing; noise is filtered here.</summary>
    public event Action<string>? Line;

    /// <summary>How many engine lines were dropped as noise. Shown, never silent.</summary>
    public long Filtered => Interlocked.Read(ref filtered);
    long filtered;

    public ConsolePipe(string session)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(session, @"^[a-z0-9_]{1,40}$"))
            throw new ArgumentException("Invalid session name");
        var (cmd, outp) = Paths(session);
        CommandPath = cmd; OutputPath = outp;
        // Both pipes must exist before cs2 starts. Two instances each: cs2 takes
        // one, and a stale waiter cannot starve it.
        command = new NamedPipeServerStream(ServerName(session, "cmd"), PipeDirection.Out, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65535, 65535);
        try
        {
            output = new NamedPipeServerStream(ServerName(session, "out"), PipeDirection.In, 2,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65535, 65535);
        }
        catch { command.Dispose(); throw; }
        live[CommandPath] = this;
        _ = Accept();
    }

    async Task Accept()
    {
        try
        {
            // Only the command side gates readiness. The output pipe is opened by
            // the engine but never written to — measured 0 bytes in every run —
            // so waiting for it would add a failure mode without any benefit.
            await command.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
            connected.TrySetResult();
            // Keep the output side accepted so the engine's handle stays valid.
            await output.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException) { }
    }

    /// <summary>Waits until CS2 has opened the command pipe.</summary>
    public Task WaitUntilConnected(TimeSpan timeout, CancellationToken token)
        => connected.Task.WaitAsync(timeout, token);

    /// <summary>Pumps a redirected stdout stream into <see cref="Line"/>.</summary>
    public async Task ReadOutput(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, false);
        var buffer = new char[8192];
        var line = new StringBuilder();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c == '\r') continue;
                    if (c != '\n') { line.Append(c); if (line.Length < 65536) continue; }
                    var text = line.ToString(); line.Clear();
                    if (Noise(text)) { Interlocked.Increment(ref filtered); continue; }
                    Line?.Invoke(text + "\n");
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException) { }
    }

    /// <summary>
    /// Engine chatter that would otherwise bury the operator's own commands.
    ///
    /// Measured on 1.41.9.1: a dedicated server prints one
    /// <c>1534.937500 Long frame (WarmupPeriod): 29.98ms elapsed, …</c> line
    /// several times per second whenever the map is not idle, and the text
    /// console logs one <c>CTextConsoleWin::GetLine</c> line per stdin poll.
    /// Both are engine health noise with no bearing on a command's result.
    /// </summary>
    static bool Noise(string line)
        => line.Contains("CTextConsoleWin::GetLine", StringComparison.Ordinal)
        || line.Contains("UNEXPECTED LONG FRAME DETECTED", StringComparison.Ordinal)
        || IsLongFrame(line);

    /// <summary>Exposed so a test can assert the filter without a live server.</summary>
    public static bool IsNoise(string line) => Noise(line);

    /// <summary>
    /// <c>&lt;uptime&gt; Long frame (&lt;phase&gt;): …</c> — matched by shape so a
    /// future phase name cannot bring the spam back.
    /// </summary>
    static bool IsLongFrame(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        // Skip the uptime number.
        var i = 0;
        while (i < trimmed.Length && (char.IsDigit(trimmed[i]) || trimmed[i] == '.')) i++;
        if (i == 0) return false;
        var rest = trimmed[i..].TrimStart();
        return rest.StartsWith("Long frame (", StringComparison.Ordinal);
    }

    /// <summary>Sends one command line. Queued by the engine; returns on write.</summary>
    public async Task Send(string text, CancellationToken token)
    {
        var payload = ConsoleProtocol.Command(text);
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(ConsolePipe));
        await writer.WaitAsync(token);
        try
        {
            if (!command.IsConnected) throw new IOException("游戏控制台管道尚未连接，无法下发命令。");
            await command.WriteAsync(payload, token);
            await command.FlushAsync(token);
        }
        finally { writer.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        live.TryRemove(CommandPath, out _);
        lifetime.Cancel();
        try { command.Dispose(); } catch (Exception) { }
        try { output.Dispose(); } catch (Exception) { }
        writer.Dispose();
        lifetime.Dispose();
    }

    /// <summary>The channel a launcher-created server is using, if it is still alive.</summary>
    public static ConsolePipe? Find(string commandPath) => live.TryGetValue(commandPath, out var pipe) ? pipe : null;

    /// <summary>The two pipe paths for a session, without touching the OS.</summary>
    public static (string Command, string Output) Paths(string session)
    {
        var prefix = @"\\.\pipe\Lantern-" + session;
        return (prefix + "-cmd", prefix + "-out");
    }

    /// <summary>The value CS2 expects after <c>-concommandpipe</c>.</summary>
    public static string Argument(string session)
    {
        var (cmd, outp) = Paths(session);
        return cmd + "," + outp;
    }

    // NamedPipeServerStream wants the bare name; the \\.\pipe\ prefix is implicit.
    static string ServerName(string session, string half) => "Lantern-" + session + "-" + half;
}
