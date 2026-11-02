using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteDesk.Agent;
using RemoteDesk.Discovery;
using RemoteDesk.Security;
using RemoteDesk.Transport;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
using var host = DeviceIdentity.CreateForTesting();
using var controller = DeviceIdentity.CreateForTesting();
Check(host.DeviceId.Length == 64 && host.DeviceId != controller.DeviceId, "Distinct identities");
Check(DeviceIdentity.ValidatePeer(host.Certificate, SslPolicyErrors.RemoteCertificateChainErrors, host.DeviceId), "Valid pin");
Check(!DeviceIdentity.ValidatePeer(host.Certificate, SslPolicyErrors.None, controller.DeviceId), "Wrong pin");
Check(!DeviceIdentity.ValidatePeer(null, SslPolicyErrors.None, host.DeviceId), "Missing certificate");
Check(DeviceIdentity.NormalizeId(host.DeviceId.ToLowerInvariant()) == host.DeviceId, "Normalize pin");
if (OperatingSystem.IsWindows())
{
    using var persistent1 = DeviceIdentity.LoadOrCreateWindows();
    using var persistent2 = DeviceIdentity.LoadOrCreateWindows();
    Check(persistent1.DeviceId == persistent2.DeviceId, "CNG identity persists across reloads");
    try { persistent1.Certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKey(); throw new Exception("Private key export allowed"); }
    catch (System.Security.Cryptography.CryptographicException) { }
    Console.WriteLine("PASS: Windows persisted non-exportable CNG identity");
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
int approvals = 0;
var established = new TaskCompletionSource<SecureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var agent = new LanAgent(host)
{
    Audit = Console.WriteLine,
    Approve = (request, _) => { Check(request.DeviceId == controller.DeviceId, "TLS identity bound to request"); Interlocked.Increment(ref approvals); return Task.FromResult(true); },
    Established = session => { established.TrySetResult(session); return Task.CompletedTask; }
};
agent.Start(0, IPAddress.Loopback);
var port = agent.Port;
await using (var client = await SecureSession.ConnectAsync("127.0.0.1", port, host.DeviceId, "Controller", controller, _ => { }, deadline.Token, requestScreen: true))
{
    client.Start(); var server = await established.Task.WaitAsync(deadline.Token);
    Check(approvals == 1 && server.PeerId == controller.DeviceId, "Approval and identities");
    Check(client.CanReceiveScreen && !client.CanSendScreen && server.CanSendScreen && !server.CanReceiveScreen, "Directional screen permission");
    Check(client.VerificationCode == server.VerificationCode, "Pairing code bound to both keys/session");
    var frame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.ScreenReceived += jpeg => frame.TrySetResult(jpeg);
    byte[] sample = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "screen.jpg"));
    await server.SendScreenAsync(sample, deadline.Token);
    Check((await frame.Task.WaitAsync(TimeSpan.FromSeconds(5))).SequenceEqual(sample), "Encrypted screen frame roundtrip");
    try { await client.SendScreenAsync(sample, deadline.Token); throw new Exception("Controller sent unauthorized screen"); }
    catch (UnauthorizedAccessException) { }
    await Task.Delay(3500, deadline.Token);
    Check(!client.Completion.IsCompleted, "Heartbeat keeps session alive");
    await client.DisconnectAsync();
    await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
}
await Task.Delay(150); // Let the server retire its session slot.
try
{
    await using var wrong = await SecureSession.ConnectAsync("127.0.0.1", port, controller.DeviceId, "Bad pin", controller, _ => { }, deadline.Token);
    throw new Exception("Wrong host pin accepted");
}
catch (AuthenticationException) { }
await Task.Delay(150);
agent.Approve = (_, _) => Task.FromResult(false);
try
{
    await using var denied = await SecureSession.ConnectAsync("127.0.0.1", port, host.DeviceId, "Rejected", controller, _ => { }, deadline.Token);
    throw new Exception("Unapproved session accepted");
}
catch (UnauthorizedAccessException) { }
await Task.Delay(150);
agent.Approve = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return true; };
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
{
    try
    {
        await using var cancelled = await SecureSession.ConnectAsync("127.0.0.1", port, host.DeviceId, "Cancelled", controller, _ => { }, cancel.Token);
        throw new Exception("Cancellation ignored");
    }
    catch (OperationCanceledException) { }
}
// Stopping an Agent during approval must cancel the wait and close the socket.
await agent.DisposeAsync();
await using var restarted = new LanAgent(host) { Approve = (_, _) => Task.FromResult(true) };
restarted.Start(port, IPAddress.Loopback);
await using var recovered = await SecureSession.ConnectAsync("127.0.0.1", port, host.DeviceId, "Restarted", controller, _ => { }, deadline.Token);
recovered.Start();
await restarted.DisposeAsync();
await recovered.Completion.WaitAsync(TimeSpan.FromSeconds(5));
// Cancel an incoming connection before TLS completes without shutting down the listener.
var authenticating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var cancellable = new LanAgent(host)
{
    Approve = (_, _) => Task.FromResult(true),
    StateChanged = state =>
    {
        if (state == RemoteDesk.Core.SessionState.Authenticating) authenticating.TrySetResult();
        if (state == RemoteDesk.Core.SessionState.Disconnected) idle.TrySetResult();
    }
};
cancellable.Start(0, IPAddress.Loopback);
using (var rawClient = new TcpClient())
{
    await rawClient.ConnectAsync(IPAddress.Loopback, cancellable.Port, deadline.Token);
    await authenticating.Task.WaitAsync(deadline.Token);
    cancellable.CancelCurrent();
    await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
}
await Task.Delay(100, deadline.Token);
await using (var afterCancel = await SecureSession.ConnectAsync("127.0.0.1", cancellable.Port, host.DeviceId, "After cancel", controller, _ => { }, deadline.Token))
{
    afterCancel.Start(); await afterCancel.DisconnectAsync();
}
Console.WriteLine("PASS: Host cancels an incomplete TLS handshake; listener remains usable");
Console.WriteLine("PASS: mTLS, pin mismatch, key binding, attended approval/rejection, heartbeat, orderly disconnect, approval cancellation, Agent restart, abrupt transport loss");

string numeric = NumericDeviceId.FromFingerprint(host.DeviceId);
Check(numeric.Length == 12 && numeric.All(char.IsAsciiDigit), "Numeric ID shape");
Check(NumericDeviceId.Normalize(NumericDeviceId.Format(numeric)) == numeric, "Formatted ID roundtrip");
var query = new DiscoveryQuery(1, Guid.NewGuid(), numeric);
var reply = DiscoveryProtocol.Sign(host, query, "Test Host", 45831);
Check(DiscoveryProtocol.Verify(reply, query, IPAddress.Loopback).Fingerprint == host.DeviceId, "Signed discovery");
try { DiscoveryProtocol.Verify(reply, query with { Nonce = Guid.NewGuid() }, IPAddress.Loopback); throw new Exception("Replayed discovery accepted"); }
catch (InvalidDataException) { }
byte[] tampered = (byte[])reply.Signature.Clone(); tampered[0] ^= 1;
try { DiscoveryProtocol.Verify(reply with { Signature = tampered }, query, IPAddress.Loopback); throw new Exception("Forged discovery accepted"); }
catch (InvalidDataException) { }
await using (var discovery = new LanDiscovery(host, 45831, "Test Host", 0, IPAddress.Loopback))
{
    discovery.Start();
    var found = await LanDiscovery.ResolveAsync(numeric, deadline.Token, [new IPEndPoint(IPAddress.Loopback, discovery.BoundPort)]);
    Check(found.Fingerprint == host.DeviceId && found.Port == 45831, "Numeric ID resolves without manual IP");
}
Console.WriteLine("PASS: screen permissions, encrypted JPEG transport, matching pairing code, numeric ID, discovery signatures/nonces and real UDP resolution");
