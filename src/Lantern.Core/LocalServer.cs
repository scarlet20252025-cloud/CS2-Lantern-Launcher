using System.Diagnostics;
using System.Text;

namespace Lantern.Core;

/// <summary>
/// A dedicated server owned by this launcher: the console pipes, the process,
/// and the stdout pump that feeds commands' output back into the pipe channel.
///
/// Order is load-bearing. CS2 opens the two pipe paths while starting up; if
/// they do not exist yet the engine asserts in
/// <c>CConsoleCommandPipes::Create</c> and writes a minidump. So the pipes are
/// created first, and only then is the process started.
///
/// The process inherits a redirected stdout because that — not the write pipe —
/// is where command output actually appears. That also means the launcher must
/// be the one starting cs2; a server launched by Steam cannot be attached.
/// </summary>
public sealed class LocalServer : IDisposable
{
    readonly CancellationTokenSource lifetime = new();
    readonly Task pump;
    int disposed;

    public ConsolePipe Pipe { get; }
    public Process Process { get; }
    public string Session { get; }
    public event Action<string>? Output;
    // Named to avoid clashing with Process.Exited inside this class.
    public event Action? ProcessExited;

    LocalServer(string session, string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        Session = session;
        Pipe = new ConsolePipe(session);
        try
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Command output only ever reaches stdout; the write pipe stays empty.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            Process = new Process { StartInfo = info, EnableRaisingEvents = true };
            Process.Exited += (_, _) => ProcessExited?.Invoke();
            Pipe.Line += line => Output?.Invoke(line);
            Process.Start();
        }
        catch { Pipe.Dispose(); lifetime.Dispose(); throw; }
        pump = Pipe.ReadOutput(Process.StandardOutput.BaseStream, lifetime.Token);
        _ = Task.Run(async () =>
        {
            try { await Process.StandardError.ReadToEndAsync(lifetime.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException) { }
        }, lifetime.Token);
    }

    /// <summary>
    /// Starts a server for a plan produced by <see cref="Hosting.Build"/>. The
    /// session name is taken from the plan's own pipe argument so the pipes and
    /// the command line can never disagree.
    /// </summary>
    public static LocalServer Start(string executable, LaunchPlan plan, string workingDirectory)
    {
        var session = SessionFrom(plan.Arguments)
            ?? throw new ArgumentException("启动参数缺少控制台管道；独立服务器必须启用控制台。");
        return new LocalServer(session, executable, plan.Arguments, workingDirectory);
    }

    /// <summary>Reads our session name back out of a <c>-concommandpipe</c> argument.</summary>
    public static string? SessionFrom(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
            if (arguments[i].Equals("-concommandpipe", StringComparison.OrdinalIgnoreCase))
                return SessionOf(arguments[i + 1]);
        return null;
    }

    static string? SessionOf(string argument)
    {
        var command = argument.Split(',')[0].Trim();
        const string prefix = @"\\.\pipe\Lantern-";
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = command[prefix.Length..];
        return rest.EndsWith("-cmd", StringComparison.OrdinalIgnoreCase) ? rest[..^4] : null;
    }

    public bool HasExited => Process.HasExited;

    /// <summary>Binds a console to this server's pipe. Call after the pipes connect.</summary>
    public GameConsole Connect() => new();

    /// <summary>
    /// Stops the server the same way a user would: ask politely through the
    /// console, then end the process if it does not leave in time. Only this
    /// exact process is killed — never a tree, never the player's client.
    /// </summary>
    public async Task<bool> Stop(TimeSpan grace)
    {
        if (Process.HasExited) return true;
        try
        {
            using var console = Connect();
            await console.Connect(Pipe.CommandPath);
            await console.Send("quit");
        }
        catch (Exception) { /* Pipe already gone: fall through to waiting. */ }
        try
        {
            await Process.WaitForExitAsync(lifetime.Token).WaitAsync(grace);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        catch (Exception e) when (e is InvalidOperationException or SystemException) { return true; }
        try
        {
            if (!Process.HasExited) Process.Kill();
            await Process.WaitForExitAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try { if (!Process.HasExited) Process.Kill(); } catch (Exception) { }
        try { Process.Dispose(); } catch (Exception) { }
        Pipe.Dispose();
        lifetime.Dispose();
        _ = pump;
    }
}
