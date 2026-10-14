using System.IO;
using System.IO.Pipes;
using System.Text;
using Serilog;

namespace Lasero.App;

/// <summary>
/// Keeps one Lasero per Windows session. A second launch (typically a double-clicked .lasero file) hands its
/// project path to the running instance over a per-user named pipe and exits, instead of opening a second
/// window that would compete for the same serial port and the same autosave snapshot. If the running
/// instance cannot be reached the second launch simply exits; it never forces a second session.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const int MaxMessageLength = 4096;

    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _listening;

    /// <summary>True for the first instance. Also true when the mutex itself could not be created: failing open
    /// keeps the application usable, at the cost of the guard.</summary>
    public bool IsPrimary { get; }

    public SingleInstanceGuard(string? scope = null)
    {
        var key = Sanitize(scope ?? Environment.UserName);
        _pipeName = "Lasero.Desktop.Open." + key;
        try
        {
            _mutex = new Mutex(false, @"Local\Lasero.Desktop.SingleInstance." + key, out var createdNew);
            IsPrimary = createdNew;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            Log.Warning(ex, "Single-instance mutex could not be created; continuing without the guard");
            IsPrimary = true;
        }
    }

    /// <summary>Sends the project path (or just an "activate" request when null) to the running instance.</summary>
    public bool TryForward(string? projectPath, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(projectPath ?? string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not reach the running Lasero instance");
            return false;
        }
    }

    /// <summary>Starts accepting forwarded launches. <paramref name="onMessage"/> runs on a pool thread with the
    /// validated project path, or null for a plain "bring the window forward" request.</summary>
    public void StartListening(Action<string?> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        if (_listening is not null) return;
        _listening = new CancellationTokenSource();
        var token = _listening.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    // A client that connects and then stalls must not block the next launch for long.
                    using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readLimit.CancelAfter(TimeSpan.FromSeconds(2));
                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    var line = await reader.ReadLineAsync(readLimit.Token).ConfigureAwait(false);
                    onMessage(ParseForwardedProjectPath(line));
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) break;
                    Log.Warning("A forwarded launch stalled and was dropped");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Receiving a forwarded launch failed");
                    try { await Task.Delay(500, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, token);
    }

    /// <summary>The only thing a forwarded launch may ask for is opening an existing-looking .lasero file by
    /// absolute path. Anything else degrades to "just activate the window".</summary>
    internal static string? ParseForwardedProjectPath(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxMessageLength) return null;
        var path = line.Trim();
        try
        {
            if (!Path.IsPathRooted(path)) return null;
            return string.Equals(Path.GetExtension(path), ".lasero", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        return builder.Length == 0 ? "user" : builder.ToString();
    }

    public void Dispose()
    {
        try { _listening?.Cancel(); }
        catch (ObjectDisposedException) { }
        _listening?.Dispose();
        _listening = null;
        _mutex?.Dispose();
    }
}

/// <summary>
/// Holds launches forwarded by second instances until the first instance has a main window to hand them to.
/// The pipe listener starts right after the mutex is taken, long before the window exists (splash, sign-in),
/// so a forwarded path must wait here instead of being lost. Thread-safe; delivery order is arrival order.
/// </summary>
internal sealed class ForwardedLaunchQueue
{
    internal const int MaxPending = 8;

    private readonly object _gate = new();
    private readonly List<string?> _pending = [];
    private Action<string?>? _handler;

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Delivers to the handler when attached, otherwise queues. A null path means "just activate".</summary>
    public void Submit(string? projectPath)
    {
        lock (_gate)
        {
            if (_handler is { } handler)
            {
                handler(projectPath);
                return;
            }
            if (_pending.Contains(projectPath, StringComparer.OrdinalIgnoreCase)) return; // repeated activate / same file
            if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
            _pending.Add(projectPath);
        }
    }

    /// <summary>Marks the application ready: flushes everything queued so far, then delivers directly.</summary>
    public void Attach(Action<string?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handler = handler;
            var queued = _pending.ToArray();
            _pending.Clear();
            foreach (var path in queued) handler(path);
        }
    }
}
