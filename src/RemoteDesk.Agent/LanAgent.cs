using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using RemoteDesk.Core;
using RemoteDesk.Security;
using RemoteDesk.Transport;

namespace RemoteDesk.Agent;

/// <summary>Attended, user-session agent. Not a privileged Windows Service.</summary>
public sealed class LanAgent(DeviceIdentity identity) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _loop;
    private int _disposed;
    private readonly object _activeGate = new();
    private CancellationTokenSource? _activeStop;
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public Func<ApprovalRequest, CancellationToken, Task<SessionGrant?>>? Authorize { get; set; }
    public Func<bool> TryReserve { get; set; } = () => true;
    public Func<ApprovalRequest, CancellationToken, Task<bool>> Approve { get; set; } = (_, _) => Task.FromResult(false);
    public Func<SecureSession, Task> Established { get; set; } = _ => Task.CompletedTask;
    public Action<SessionState>? StateChanged { get; set; }
    public Action<string>? Audit { get; set; }
    public void Start(int port = 45831, IPAddress? bindAddress = null)
    {
        if (_loop is not null) throw new InvalidOperationException("Agent already started.");
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
        _listener.Start(4);
        _loop = RunAsync();
    }
    private async Task RunAsync()
    {
        // One slot; extra clients are immediately closed, never queued for automatic approval.
        Task? active = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (active is { IsCompleted: false }) { client.Dispose(); continue; }
                if (active is not null) await active.ConfigureAwait(false);
                if (!TryReserve()) { client.Dispose(); continue; }
                active = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Audit?.Invoke("Listener stopped unexpectedly."); }
        finally { if (active is not null) await active.ConfigureAwait(false); }
    }
    public void CancelCurrent()
    {
        CancellationTokenSource? current;
        lock (_activeGate) current = _activeStop;
        try { current?.Cancel(); }
        catch (ObjectDisposedException) { /* The session completed concurrently. */ }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using var activeStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        lock (_activeGate) _activeStop = activeStop;
        try
        {
            await using var session = await SecureSession.AcceptAsync(client, identity, Approve,
                state => StateChanged?.Invoke(state), activeStop.Token, Authorize).ConfigureAwait(false);
            if (session is null) { Audit?.Invoke("Incoming request rejected or expired."); return; }
            Audit?.Invoke("Attended LAN session approved.");
            await Established(session).ConfigureAwait(false);
            session.Start();
            await session.Completion.WaitAsync(activeStop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        { Audit?.Invoke(_stop.IsCancellationRequested ? "Agent stopped." : $"Incoming connection ended or failed authentication ({ex.GetType().Name})."); }
        finally
        {
            lock (_activeGate) _activeStop = null;
            client.Dispose(); StateChanged?.Invoke(SessionState.Disconnected);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _listener?.Stop();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
