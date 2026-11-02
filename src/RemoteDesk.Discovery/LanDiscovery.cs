using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDesk.Security;

namespace RemoteDesk.Discovery;

public sealed record DiscoveredDevice(string Code, string Name, string Fingerprint, IPAddress Address, int Port);
public sealed record DiscoveryQuery([property: JsonRequired] int Version, [property: JsonRequired] Guid Nonce, [property: JsonRequired] string Code);
public sealed record DiscoveryReply([property: JsonRequired] int Version, [property: JsonRequired] Guid Nonce,
    [property: JsonRequired] string Code, [property: JsonRequired] string Name, [property: JsonRequired] int Port,
    [property: JsonRequired] byte[] Certificate, [property: JsonRequired] byte[] Signature);

public static class DiscoveryProtocol
{
    public const int Port = 45832;
    public const int MaxDatagram = 4096;
    public static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 4
    };
    private static byte[] SignedData(Guid nonce, string code, string name, int port, string fingerprint) =>
        Encoding.UTF8.GetBytes($"RemoteDesk.Discovery.v1\n{nonce:N}\n{code}\n{name}\n{port}\n{fingerprint}");
    public static DiscoveryReply Sign(DeviceIdentity identity, DiscoveryQuery query, string name, int tcpPort)
    {
        string code = NumericDeviceId.FromFingerprint(identity.DeviceId);
        if (query.Version != 1 || query.Nonce == Guid.Empty || query.Code != code) throw new InvalidDataException("Invalid discovery query.");
        using var key = identity.Certificate.GetRSAPrivateKey()!;
        return new(1, query.Nonce, code, name, tcpPort, identity.Certificate.RawData,
            key.SignData(SignedData(query.Nonce, code, name, tcpPort, identity.DeviceId), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    public static DiscoveredDevice Verify(DiscoveryReply reply, DiscoveryQuery query, IPAddress address)
    {
        if (reply.Version != 1 || reply.Nonce != query.Nonce || reply.Code != query.Code || reply.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(reply.Name) || reply.Name.Length > 64 || reply.Name.Any(char.IsControl) ||
            reply.Certificate is not { Length: > 0 and <= 2048 } || reply.Signature is not { Length: > 0 and <= 1024 })
            throw new InvalidDataException("Invalid discovery response.");
        using var cert = X509CertificateLoader.LoadCertificate(reply.Certificate);
        string pin = DeviceIdentity.Fingerprint(cert);
        if (NumericDeviceId.FromFingerprint(pin) != reply.Code || !DeviceIdentity.ValidatePeer(cert, SslPolicyErrors.RemoteCertificateChainErrors, pin))
            throw new InvalidDataException("Discovery identity mismatch.");
        using var key = cert.GetRSAPublicKey()!;
        if (!key.VerifyData(SignedData(reply.Nonce, reply.Code, reply.Name, reply.Port, pin), reply.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Invalid discovery signature.");
        return new(reply.Code, reply.Name, pin, address, reply.Port);
    }
    public static IEnumerable<IPEndPoint> BroadcastTargets()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        foreach (var entry in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var ip = entry.Address.GetAddressBytes(); var mask = entry.IPv4Mask.GetAddressBytes();
            if (mask.All(b => b == 255)) continue;
            addresses.Add(new IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray()));
        }
        return addresses.Select(a => new IPEndPoint(a, Port));
    }
}

/// <summary>Link-local discovery only. Responses bind a fresh query nonce to a signed public key, not to a central account.</summary>
public sealed class LanDiscovery : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly int _tcpPort;
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private readonly UdpClient _udp;
    private Task? _loop;
    public int BoundPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public LanDiscovery(DeviceIdentity identity, int tcpPort, string name, int udpPort = DiscoveryProtocol.Port, IPAddress? bind = null)
    {
        _identity = identity; _tcpPort = tcpPort; _name = name;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        try { _udp.Client.Bind(new IPEndPoint(bind ?? IPAddress.Any, udpPort)); }
        catch { _udp.Dispose(); _stop.Dispose(); throw; }
    }
    public void Start() => _loop = _loop is null ? RunAsync() : throw new InvalidOperationException("Discovery already started.");
    private async Task RunAsync()
    {
        long window = Environment.TickCount64; int replies = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _udp.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (packet.Buffer.Length is < 2 or > DiscoveryProtocol.MaxDatagram) continue;
                if (Environment.TickCount64 - window >= 1000) { window = Environment.TickCount64; replies = 0; }
                if (replies >= 8) continue;
                try
                {
                    var query = JsonSerializer.Deserialize<DiscoveryQuery>(packet.Buffer, DiscoveryProtocol.Json);
                    if (query is null || query.Code != NumericDeviceId.FromFingerprint(_identity.DeviceId)) continue;
                    var response = DiscoveryProtocol.Sign(_identity, query, _name, _tcpPort);
                    replies++;
                    await _udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response, DiscoveryProtocol.Json), packet.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or CryptographicException or ArgumentException or SocketException) { /* Ignore invalid local datagrams. */ }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    public static async Task<DiscoveredDevice> ResolveAsync(string numericId, CancellationToken token, IEnumerable<IPEndPoint>? targets = null)
    {
        string code = NumericDeviceId.Normalize(numericId);
        var query = new DiscoveryQuery(1, Guid.NewGuid(), code);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(query, DiscoveryProtocol.Json);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var endpoints = (targets ?? DiscoveryProtocol.BroadcastTargets()).Distinct().ToArray();
        var devices = new Dictionary<string, DiscoveredDevice>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var sends = SendQueriesAsync();
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (packet.Buffer.Length is < 2 or > DiscoveryProtocol.MaxDatagram) continue;
                try
                {
                    var reply = JsonSerializer.Deserialize<DiscoveryReply>(packet.Buffer, DiscoveryProtocol.Json);
                    if (reply is not null)
                    {
                        var device = DiscoveryProtocol.Verify(reply, query, packet.RemoteEndPoint.Address);
                        devices.TryAdd(device.Fingerprint, device);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or CryptographicException or ArgumentException) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        finally { timeout.Cancel(); await sends.ConfigureAwait(false); }
        token.ThrowIfCancellationRequested();
        if (devices.Count > 1) throw new InvalidDataException("Multiple keys claim that numeric ID. Do not connect; verify full fingerprints using Advanced setup.");
        return devices.Values.SingleOrDefault() ?? throw new IOException("Device not found on this LAN. Keep RemoteDesk open on the Host, enable availability, and allow TCP 45831 + UDP 45832 on Private networks. Internet ID routing is not configured.");

        async Task SendQueriesAsync()
        {
            try
            {
                for (int round = 0; round < 4; round++)
                {
                    foreach (var endpoint in endpoints)
                    {
                        try { await udp.SendAsync(bytes, endpoint, timeout.Token).ConfigureAwait(false); }
                        catch (SocketException) { /* One disconnected adapter must not block other adapters. */ }
                    }
                    await Task.Delay(500, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _udp.Dispose();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
