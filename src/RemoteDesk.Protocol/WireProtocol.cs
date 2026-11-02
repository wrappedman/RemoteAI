using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk.Protocol;

public enum MessageType { ConnectionRequest, Approval, Rejection, Ping, Pong, Disconnect }
public sealed record Payload(string? DeviceId = null, string? DisplayName = null);
public sealed record Envelope(int Version, Guid MessageId, Guid SessionId, long Sequence, MessageType Type, Payload Payload);

public static class WireProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 16 * 1024;
    public const int MaxMessagesPerSession = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter<MessageType>(null, false) }
    };

    public static Envelope Create(Guid sessionId, long sequence, MessageType type, Payload? payload = null) =>
        new(Version, Guid.NewGuid(), sessionId, sequence, type, payload ?? new());

    public static void Validate(Envelope message)
    {
        if (message.Version != Version || message.MessageId == Guid.Empty || message.SessionId == Guid.Empty ||
            message.Sequence < 1 || message.Payload is null || !Enum.IsDefined(message.Type))
            throw new InvalidDataException("Invalid protocol envelope.");
        if (message.Type == MessageType.ConnectionRequest)
        {
            if (message.Payload.DeviceId is not { Length: 64 } id || !id.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(message.Payload.DisplayName) || message.Payload.DisplayName.Length > 64 ||
                message.Payload.DisplayName.Any(char.IsControl))
                throw new InvalidDataException("Invalid connection request.");
        }
        else if (message.Payload.DeviceId is not null || message.Payload.DisplayName is not null)
            throw new InvalidDataException("Unexpected message payload.");
    }

    public static async Task WriteAsync(Stream stream, Envelope message, CancellationToken token)
    {
        Validate(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Frame exceeds size limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<Envelope> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size < 2 || size > MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        try
        {
            var result = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Missing envelope.");
            Validate(result);
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("Malformed protocol message.", ex); }
    }
}

/// <summary>Per-direction, per-session ordering and replay protection, with a bounded session lifetime.</summary>
public sealed class MessageGuard(Guid sessionId)
{
    private long _sequence;
    private readonly HashSet<Guid> _ids = [];
    public void Accept(Envelope message)
    {
        WireProtocol.Validate(message);
        if (message.SessionId != sessionId || message.Sequence != _sequence + 1 ||
            _ids.Count >= WireProtocol.MaxMessagesPerSession || !_ids.Add(message.MessageId))
            throw new InvalidDataException("Session, sequence or replay check failed.");
        _sequence = message.Sequence;
    }
}
