using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteDesk.Agent;
using RemoteDesk.Security;
using RemoteDesk.Transport;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
using var host = DeviceIdentity.CreateEphemeral();
using var controller = DeviceIdentity.CreateEphemeral();
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
    Approve = (request, _) => { Check(request.DeviceId == controller.DeviceId, "TLS identity bound to request"); Interlocked.Increment(ref approvals); return Task.FromResult(true); },
    Established = session => { established.TrySetResult(session); return Task.CompletedTask; }
};
agent.Start(0, IPAddress.Loopback);
var port = agent.Port;
await using (var client = await SecureSession.ConnectAsync("127.0.0.1", port, host.DeviceId, "Controller", controller, _ => { }, deadline.Token))
{
    client.Start(); var server = await established.Task.WaitAsync(deadline.Token);
    Check(approvals == 1 && server.PeerId == controller.DeviceId, "Approval and identities");
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
Console.WriteLine("PASS: mTLS, pin mismatch, key binding, attended approval/rejection, heartbeat, orderly disconnect, approval cancellation, Agent restart, abrupt transport loss");
