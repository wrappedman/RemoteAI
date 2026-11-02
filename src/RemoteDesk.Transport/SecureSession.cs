using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Security;

namespace RemoteDesk.Transport;

public sealed class SecureSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly Guid _id;
    private readonly MessageGuard _guard;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence = 1;
    private int _started;
    private int _closed;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    public string PeerId { get; }
    public string PeerName { get; }
    public string Cipher => $"{_stream.SslProtocol} / {_stream.NegotiatedCipherSuite}";
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public Task Completion => _completion.Task;
    public string CloseReason { get; private set; } = "Disconnected";
    public event Action<string>? Activity;

    private SecureSession(TcpClient client, SslStream stream, Guid id, MessageGuard guard, string peerId, string name)
    {
        _client = client; _stream = stream; _id = id; _guard = guard; PeerId = peerId; PeerName = name;
    }

    public static async Task<SecureSession> ConnectAsync(string address, int port, string expectedHostId,
        string displayName, DeviceIdentity identity, Action<SessionState> state, CancellationToken token)
    {
        expectedHostId = DeviceIdentity.NormalizeId(expectedHostId);
        var client = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            state(SessionState.Connecting);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(address, port, connectTimeout.Token);
            state(SessionState.Authenticating);
            stream = new SslStream(client.GetStream(), false, (_, cert, _, errors) => DeviceIdentity.ValidatePeer(cert, errors, expectedHostId));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "RemoteDesk", ClientCertificates = new X509CertificateCollection { identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false
            }, connectTimeout.Token);
            state(SessionState.WaitingForApproval);
            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            approvalTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var id = Guid.NewGuid();
            await WireProtocol.WriteAsync(stream, WireProtocol.Create(id, 1, MessageType.ConnectionRequest,
                new(identity.DeviceId, displayName)), approvalTimeout.Token);
            var guard = new MessageGuard(id);
            var response = await WireProtocol.ReadAsync(stream, approvalTimeout.Token);
            guard.Accept(response);
            if (response.Type == MessageType.Rejection) throw new UnauthorizedAccessException("The Host declined the request.");
            if (response.Type != MessageType.Approval) throw new InvalidDataException("Expected attended approval.");
            state(SessionState.Connected);
            return new(client, stream, id, guard, expectedHostId, address);
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }

    public static async Task<SecureSession?> AcceptAsync(TcpClient client, DeviceIdentity identity,
        Func<ApprovalRequest, CancellationToken, Task<bool>> approve, Action<SessionState> state, CancellationToken token)
    {
        SslStream? stream = null;
        try
        {
            client.NoDelay = true;
            state(SessionState.Connecting);
            state(SessionState.Authenticating);
            stream = new SslStream(client.GetStream(), false, (_, cert, _, errors) => DeviceIdentity.ValidatePeer(cert, errors, null));
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck, AllowRenegotiation = false
            }, handshakeTimeout.Token);
            var request = await WireProtocol.ReadAsync(stream, handshakeTimeout.Token);
            var guard = new MessageGuard(request.SessionId);
            guard.Accept(request);
            var peerId = DeviceIdentity.Fingerprint(stream.RemoteCertificate!);
            if (request.Type != MessageType.ConnectionRequest || request.Payload.DeviceId != peerId)
                throw new InvalidDataException("Request identity does not match TLS identity.");
            state(SessionState.WaitingForApproval);
            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            approvalTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            bool accepted;
            try
            {
                accepted = await approve(new(peerId, request.Payload.DisplayName!, client.Client.RemoteEndPoint?.ToString() ?? "Unknown"),
                    approvalTimeout.Token).WaitAsync(approvalTimeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { accepted = false; }
            using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            replyTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await WireProtocol.WriteAsync(stream, WireProtocol.Create(request.SessionId, 1,
                accepted ? MessageType.Approval : MessageType.Rejection), replyTimeout.Token);
            if (!accepted) { stream.Dispose(); client.Dispose(); return null; }
            state(SessionState.Connected);
            return new(client, stream, request.SessionId, guard, peerId, request.Payload.DisplayName!);
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _receiveTask = ReceiveAsync();
        _heartbeatTask = HeartbeatAsync();
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var message = await WireProtocol.ReadAsync(_stream, timeout.Token);
                _guard.Accept(message);
                switch (message.Type)
                {
                    case MessageType.Ping: await SendAsync(MessageType.Pong, _stop.Token); break;
                    case MessageType.Pong: Activity?.Invoke("Heartbeat acknowledged"); break;
                    case MessageType.Disconnect: Close("Peer disconnected"); return;
                    default: throw new InvalidDataException("Unexpected message in connected state.");
                }
            }
        }
        catch (OperationCanceledException) { Close(_stop.IsCancellationRequested ? CloseReason : "Heartbeat timeout"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or AuthenticationException or ObjectDisposedException)
        { Close(ex is InvalidDataException ? "Invalid protocol message" : "Connection lost"); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(_stop.Token)) await SendAsync(MessageType.Ping, _stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or ObjectDisposedException) { Close("Connection lost"); }
    }
    private async Task SendAsync(MessageType type, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _writeGate.WaitAsync(timeout.Token);
        try
        {
            if (_sequence >= WireProtocol.MaxMessagesPerSession) throw new InvalidDataException("Session message limit reached. Reconnect with fresh approval.");
            await WireProtocol.WriteAsync(_stream, WireProtocol.Create(_id, ++_sequence, type), timeout.Token);
        }
        finally { _writeGate.Release(); }
    }
    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try { await SendAsync(MessageType.Disconnect, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { Close("Disconnected locally"); }
    }
    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        _stop.Cancel();
        _stream.Dispose();
        _client.Dispose();
        _completion.TrySetResult();
    }
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        if (_receiveTask is not null) await _receiveTask;
        if (_heartbeatTask is not null) await _heartbeatTask;
        // No producers remain after both loops finish.
        _writeGate.Dispose(); _stop.Dispose();
    }
}
