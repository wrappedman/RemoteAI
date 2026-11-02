using System.Buffers.Binary;
using System.Text;
using RemoteDesk.Protocol;
static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Invalid(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("Expected protocol rejection");
}
static async Task InvalidRead(byte[] bytes)
{
    try { await WireProtocol.ReadAsync(new MemoryStream(bytes), default); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return; }
    throw new Exception("Invalid frame accepted");
}
var id = Guid.NewGuid();
var request = WireProtocol.Create(id, 1, MessageType.ConnectionRequest, new(new string('A', 64), "Test machine"));
using var stream = new MemoryStream();
await WireProtocol.WriteAsync(stream, request, default);
stream.Position = 0;
Check(await WireProtocol.ReadAsync(stream, default) == request);
var guard = new MessageGuard(id); guard.Accept(request);
Invalid(() => guard.Accept(request));
Invalid(() => guard.Accept(WireProtocol.Create(id, 3, MessageType.Ping)));
Invalid(() => guard.Accept(WireProtocol.Create(Guid.NewGuid(), 2, MessageType.Ping)));
Invalid(() => guard.Accept(request with { Sequence = 2 })); // Reused message ID, new sequence.
guard.Accept(WireProtocol.Create(id, 2, MessageType.Ping));
Invalid(() => WireProtocol.Validate(request with { Version = 999 }));
Invalid(() => WireProtocol.Validate(request with { Payload = new("bad", "name") }));
Invalid(() => WireProtocol.Validate(request with { Payload = new(new string('A', 64), "bad\nname") }));
Invalid(() => WireProtocol.Validate(WireProtocol.Create(id, 1, MessageType.Ping, new("unexpected"))));
foreach (int length in new[] { -1, 0, 1, WireProtocol.MaxFrameBytes + 1, int.MaxValue })
{
    var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, length); await InvalidRead(header);
}
await InvalidRead([0, 0, 0]);
await InvalidRead([0, 0, 0, 10, 1, 2]);
foreach (var text in new[] { "null", "{}", "not json", "{\"version\":2,\"unknown\":true}" })
{
    var bytes = Encoding.UTF8.GetBytes(text); var frame = new byte[4 + bytes.Length];
    BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length); bytes.CopyTo(frame, 4); await InvalidRead(frame);
}
// Missing type must not silently default to ConnectionRequest; duplicate JSON keys are forbidden.
string valid = Encoding.UTF8.GetString(stream.ToArray().AsSpan(4));
foreach (string invalid in new[] {
    valid.Replace("\"type\":\"ConnectionRequest\",", ""),
    valid.Replace("\"version\":2", "\"version\":2,\"version\":2"),
    valid.Replace("\"ConnectionRequest\"", "0") })
{
    var bytes = Encoding.UTF8.GetBytes(invalid); var frame = new byte[4 + bytes.Length];
    BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length); bytes.CopyTo(frame, 4); await InvalidRead(frame);
}
var bounded = new MessageGuard(id);
for (int n = 1; n <= WireProtocol.MaxMessagesPerSession; n++) bounded.Accept(WireProtocol.Create(id, n, MessageType.Ping));
Invalid(() => bounded.Accept(WireProtocol.Create(id, WireProtocol.MaxMessagesPerSession + 1, MessageType.Ping)));
using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
try { await WireProtocol.ReadAsync(new MemoryStream(), cancellation.Token); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { }
Console.WriteLine("PASS: roundtrip, replay, sequence, session, version, invalid payload, oversized/negative/truncated/malformed frames, cancellation");

Invalid(() => WireProtocol.Validate(WireProtocol.Create(id, 1, MessageType.ScreenFrame, new(Jpeg: new byte[30]))));
Invalid(() => WireProtocol.Validate(WireProtocol.Create(id, 1, MessageType.Ping, new(ViewScreen: true))));
Console.WriteLine("PASS: protocol v2 screen payload validation and approval-only capability grants");
