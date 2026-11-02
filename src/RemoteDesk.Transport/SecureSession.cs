using System.Net.Security;
using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private Task? _producerTask;
    private int _producerStarted;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteCommand>> _pending = new();
    private readonly Channel<RemoteCommand> _commands = Channel.CreateBounded<RemoteCommand>(32);
    private Task? _commandTask;
    public bool IsHost { get; }
    public SessionGrant Permissions { get; }
    public Func<RemoteCommand, CancellationToken, Task<RemoteCommand>>? CommandHandler { get; set; }
    public event Action<InputEvent>? InputReceived;
    public bool CanSendScreen { get; }
    public bool CanReceiveScreen { get; }
    public string VerificationCode { get; }
    public event Action<byte[]>? ScreenReceived;
    public event Action? ScreenUnavailable;
    public string PeerId { get; }
    public string PeerName { get; }
    public string Cipher => $"{_stream.SslProtocol} / {_stream.NegotiatedCipherSuite}";
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public Task Completion => _completion.Task;
    public string CloseReason { get; private set; } = "Disconnected";
    public event Action<string>? Activity;

    private SecureSession(TcpClient client, SslStream stream, Guid id, MessageGuard guard, string peerId, string name, bool canSendScreen, bool canReceiveScreen, string verificationCode, bool isHost, SessionGrant permissions)
    {
        _client = client; _stream = stream; _id = id; _guard = guard; PeerId = peerId; PeerName = name;
        IsHost = isHost; Permissions = permissions;
        CanSendScreen = canSendScreen; CanReceiveScreen = canReceiveScreen; VerificationCode = verificationCode;
    }

    public static async Task<SecureSession> ConnectAsync(string address, int port, string expectedHostId,
        string displayName, DeviceIdentity identity, Action<SessionState> state, CancellationToken token, bool requestScreen = false, Action<string>? verificationCode = null, SessionGrant? requested = null, string? password = null)
    {
        requested ??= new(requestScreen);
        expectedHostId = DeviceIdentity.NormalizeId(expectedHostId);
        var client = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            state(SessionState.Connecting);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(address, port, connectTimeout.Token).ConfigureAwait(false);
            state(SessionState.Authenticating);
            stream = new SslStream(client.GetStream(), false, (_, cert, _, errors) => DeviceIdentity.ValidatePeer(cert, errors, expectedHostId));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "RemoteDesk", ClientCertificates = new X509CertificateCollection { identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false
            }, connectTimeout.Token).ConfigureAwait(false);
            state(SessionState.WaitingForApproval);
            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            approvalTimeout.CancelAfter(TimeSpan.FromSeconds(135));
            var id = Guid.NewGuid();
            string code = NumericDeviceId.VerificationCode(id, identity.DeviceId, expectedHostId);
            verificationCode?.Invoke(code);
            await WireProtocol.WriteAsync(stream, WireProtocol.Create(id, 1, MessageType.ConnectionRequest,
                new(identity.DeviceId, displayName, requested.ViewScreen, ControlInput: requested.ControlInput, FileAccess: requested.FileAccess, DesktopEffects: requested.DesktopEffects, Password: password)), approvalTimeout.Token).ConfigureAwait(false);
            var guard = new MessageGuard(id);
            var response = await WireProtocol.ReadAsync(stream, approvalTimeout.Token).ConfigureAwait(false);
            guard.Accept(response);
            if (response.Type == MessageType.Rejection) throw new UnauthorizedAccessException("The Host declined the request.");
            if (response.Type != MessageType.Approval) throw new InvalidDataException("Expected attended approval.");
            token.ThrowIfCancellationRequested();
            if ((response.Payload.ViewScreen && !requested.ViewScreen) || (response.Payload.ControlInput && !requested.ControlInput) ||
                (response.Payload.FileAccess && !requested.FileAccess) || (response.Payload.DesktopEffects && !requested.DesktopEffects)) throw new InvalidDataException("Unexpected screen permission.");
            state(SessionState.Connected);
            return new(client, stream, id, guard, expectedHostId, address, false, response.Payload.ViewScreen, code, false, new(response.Payload.ViewScreen, response.Payload.ControlInput, response.Payload.FileAccess, response.Payload.DesktopEffects));
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }

    public static async Task<SecureSession?> AcceptAsync(TcpClient client, DeviceIdentity identity,
        Func<ApprovalRequest, CancellationToken, Task<bool>> approve, Action<SessionState> state, CancellationToken token, Func<ApprovalRequest, CancellationToken, Task<SessionGrant?>>? authorize = null)
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
            }, handshakeTimeout.Token).ConfigureAwait(false);
            var request = await WireProtocol.ReadAsync(stream, handshakeTimeout.Token).ConfigureAwait(false);
            var guard = new MessageGuard(request.SessionId);
            guard.Accept(request);
            var peerId = DeviceIdentity.Fingerprint(stream.RemoteCertificate!);
            if (request.Type != MessageType.ConnectionRequest || request.Payload.DeviceId != peerId)
                throw new InvalidDataException("Request identity does not match TLS identity.");
            state(SessionState.WaitingForApproval);
            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            approvalTimeout.CancelAfter(TimeSpan.FromSeconds(120));
            SessionGrant? grant;
            try
            {
                var approval = new ApprovalRequest(peerId, request.Payload.DisplayName!, client.Client.RemoteEndPoint?.ToString() ?? "Unknown",
                    NumericDeviceId.VerificationCode(request.SessionId, peerId, identity.DeviceId), request.Payload.ViewScreen,
                    request.Payload.ControlInput, request.Payload.FileAccess, request.Payload.DesktopEffects, request.Payload.Password);
                grant = authorize is not null ? await authorize(approval, approvalTimeout.Token).WaitAsync(approvalTimeout.Token).ConfigureAwait(false)
                    : await approve(approval, approvalTimeout.Token).WaitAsync(approvalTimeout.Token).ConfigureAwait(false) ? new(request.Payload.ViewScreen) : null;
                if (grant is not null && ((grant.ViewScreen && !approval.ViewScreen) || (grant.ControlInput && !approval.ControlInput) ||
                    (grant.FileAccess && !approval.FileAccess) || (grant.DesktopEffects && !approval.DesktopEffects)))
                    throw new InvalidDataException("Consent exceeds requested capabilities.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { grant = null; }
            using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            replyTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await WireProtocol.WriteAsync(stream, WireProtocol.Create(request.SessionId, 1,
                grant is not null ? MessageType.Approval : MessageType.Rejection, new(ViewScreen: grant?.ViewScreen == true, ControlInput: grant?.ControlInput == true, FileAccess: grant?.FileAccess == true, DesktopEffects: grant?.DesktopEffects == true)), replyTimeout.Token).ConfigureAwait(false);
            if (grant is null) { stream.Dispose(); client.Dispose(); return null; }
            token.ThrowIfCancellationRequested();
            state(SessionState.Connected);
            return new(client, stream, request.SessionId, guard, peerId, request.Payload.DisplayName!, grant.ViewScreen, false,
                NumericDeviceId.VerificationCode(request.SessionId, peerId, identity.DeviceId), true, grant);
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _commandTask = HandleCommandsAsync();
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
                var message = await WireProtocol.ReadAsync(_stream, timeout.Token).ConfigureAwait(false);
                _guard.Accept(message);
                switch (message.Type)
                {
                    case MessageType.Ping: await SendAsync(MessageType.Pong, _stop.Token).ConfigureAwait(false); break;
                    case MessageType.Pong: Activity?.Invoke("Heartbeat acknowledged"); break;
                    case MessageType.ScreenFrame when CanReceiveScreen:
                        ScreenReceived?.Invoke(message.Payload.Jpeg!); break;
                    case MessageType.ScreenUnavailable when CanReceiveScreen:
                        ScreenUnavailable?.Invoke(); break;
                    case MessageType.Input when IsHost && Permissions.ControlInput:
                        InputReceived?.Invoke(message.Payload.Input!); break;
                    case MessageType.Command:
                        if (!_commands.Writer.TryWrite(message.Payload.Command!)) throw new InvalidDataException("Command queue full.");
                        break;
                    case MessageType.Reply:
                        var reply = message.Payload.Command!;
                        if (_pending.TryRemove(reply.Id, out var promise)) promise.TrySetResult(reply);
                        break;
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
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) await SendAsync(MessageType.Ping, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or ObjectDisposedException) { Close("Connection lost"); }
    }
    private static bool IsFileOperation(Operation operation) => operation is Operation.ListFiles or Operation.ReadInfo or Operation.ReadChunk
        or Operation.WriteBegin or Operation.WriteChunk or Operation.WriteComplete or Operation.ShareInfo;
    private bool Allowed(Operation operation) => operation switch
    {
        Operation.SetStream => Permissions.ViewScreen,
        Operation.RequestAdmin or Operation.RequestInputLock => Permissions.ControlInput,
        _ => Permissions.FileAccess
    };
    private async Task HandleCommandsAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                RemoteCommand response;
                try
                {
                    response = !Allowed(command.Operation) || (!IsHost && !IsFileOperation(command.Operation)) ? command.Fail("Permission denied by peer.")
                        : CommandHandler is null ? command.Fail("The peer has not shared a folder for this session.")
                        : await CommandHandler(command, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { response = command.Fail("Operation failed or path is not permitted."); }
                await SendAsync(MessageType.Reply, _stop.Token, new(Command: response)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException) { }
    }
    public Task SendInputAsync(InputEvent input, CancellationToken token)
    {
        if (IsHost || !Permissions.ControlInput) throw new UnauthorizedAccessException("Input control was not granted.");
        return SendAsync(MessageType.Input, token, new(Input: input));
    }
    public async Task<RemoteCommand> RequestAsync(RemoteCommand command, CancellationToken token)
    {
        if (!Allowed(command.Operation) || (IsHost && !IsFileOperation(command.Operation))) throw new UnauthorizedAccessException("Operation was not granted.");
        if (_pending.Count >= 16) throw new IOException("Too many pending requests.");
        var promise = new TaskCompletionSource<RemoteCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(command.Id, promise)) throw new InvalidOperationException("Duplicate request.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(command.Operation is Operation.ReadInfo or Operation.WriteBegin or Operation.WriteComplete ? 600 : 120));
        try
        {
            await SendAsync(MessageType.Command, timeout.Token, new(Command: command)).ConfigureAwait(false);
            var reply = await promise.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (reply.Operation != command.Operation) throw new InvalidDataException("Reply operation mismatch.");
            if (reply.Error is not null) throw new IOException(reply.Error);
            return reply;
        }
        finally { _pending.TryRemove(command.Id, out _); }
    }
    public void StartScreenProducer(Func<CancellationToken, Task> producer)
    {
        if (!CanSendScreen || Interlocked.Exchange(ref _producerStarted, 1) != 0)
            throw new InvalidOperationException("Only the approved Host may start one screen producer.");
        _producerTask = Task.Run(() => producer(_stop.Token));
    }
    public Task SendScreenAsync(byte[] jpeg, CancellationToken token)
    {
        if (!CanSendScreen) throw new UnauthorizedAccessException("Screen sharing was not approved for this sender.");
        return SendAsync(MessageType.ScreenFrame, token, new(Jpeg: jpeg));
    }
    public Task NotifyScreenUnavailableAsync(CancellationToken token)
    {
        if (!CanSendScreen) throw new UnauthorizedAccessException("Screen sharing was not approved.");
        return SendAsync(MessageType.ScreenUnavailable, token);
    }
    private async Task SendAsync(MessageType type, CancellationToken token, Payload? payload = null)
    {
        if (Volatile.Read(ref _closed) != 0) throw new IOException("Session is closed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        if (type == MessageType.ScreenFrame)
        {
            if (!await _writeGate.WaitAsync(0, timeout.Token).ConfigureAwait(false)) return;
        }
        else await _writeGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (_sequence >= WireProtocol.MaxMessagesPerSession) throw new InvalidDataException("Session message limit reached. Reconnect with fresh approval.");
            await WireProtocol.WriteAsync(_stream, WireProtocol.Create(_id, ++_sequence, type, payload), timeout.Token).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }
    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try { await SendAsync(MessageType.Disconnect, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { Close("Disconnected locally"); }
    }
    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        _stop.Cancel(); _commands.Writer.TryComplete();
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _stream.Dispose();
        _client.Dispose();
        _completion.TrySetResult();
    }
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
        if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false);
        if (_commandTask is not null) await _commandTask.ConfigureAwait(false);
        if (_producerTask is not null) await _producerTask.ConfigureAwait(false);
        // No producers remain after both loops finish.
        _writeGate.Dispose(); _stop.Dispose();
    }
}
