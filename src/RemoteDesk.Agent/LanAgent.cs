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
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
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
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token);
                if (active is { IsCompleted: false }) { client.Dispose(); continue; }
                if (active is not null) await active;
                if (!TryReserve()) { client.Dispose(); continue; }
                active = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Audit?.Invoke("Listener stopped unexpectedly."); }
        finally { if (active is not null) await active; }
    }
    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            await using var session = await SecureSession.AcceptAsync(client, identity, Approve,
                state => StateChanged?.Invoke(state), _stop.Token);
            if (session is null) { Audit?.Invoke("Incoming request rejected or expired."); return; }
            Audit?.Invoke("Attended LAN session approved.");
            await Established(session);
            session.Start();
            await session.Completion.WaitAsync(_stop.Token);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        { Audit?.Invoke(_stop.IsCancellationRequested ? "Agent stopped." : "Incoming connection ended or failed authentication."); }
        finally { client.Dispose(); StateChanged?.Invoke(SessionState.Disconnected); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _listener?.Stop();
        if (_loop is not null) await _loop;
        _stop.Dispose();
    }
}
