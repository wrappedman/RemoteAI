using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk.Protocol;

public enum MessageType { ConnectionRequest, Approval, Rejection, Ping, Pong, Disconnect, ScreenFrame, ScreenUnavailable }
public sealed record Payload(string? DeviceId = null, string? DisplayName = null, bool ViewScreen = false, byte[]? Jpeg = null);
public sealed record Envelope(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid MessageId,
    [property: JsonRequired] Guid SessionId,
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] MessageType Type,
    [property: JsonRequired] Payload Payload);

public static class WireProtocol
{
    public const int Version = 2;
    public const int MaxFrameBytes = 384 * 1024;
    public const int MaxJpegBytes = 256 * 1024;
    public const int MaxMessagesPerSession = 100000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter<MessageType>(null, false) }
    };

    public static Envelope Create(Guid sessionId, long sequence, MessageType type, Payload? payload = null) =>
        new(Version, Guid.NewGuid(), sessionId, sequence, type, payload ?? new());

    public static void Validate(Envelope message)
    {
        if (message.Version != Version || message.MessageId == Guid.Empty || message.SessionId == Guid.Empty ||
            message.Sequence < 1 || message.Payload is null || !Enum.IsDefined(message.Type))
            throw new InvalidDataException("Invalid protocol envelope.");
        var payload = message.Payload;
        if (message.Type == MessageType.ConnectionRequest)
        {
            if (payload.DeviceId is not { Length: 64 } id || !id.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(payload.DisplayName) || payload.DisplayName.Length > 64 ||
                payload.DisplayName.Any(char.IsControl) || payload.Jpeg is not null)
                throw new InvalidDataException("Invalid connection request.");
        }
        else if (message.Type == MessageType.ScreenFrame)
        {
            if (payload.DeviceId is not null || payload.DisplayName is not null || payload.ViewScreen || payload.Jpeg is null)
                throw new InvalidDataException("Invalid screen payload.");
            JpegFrame.Validate(payload.Jpeg);
        }
        else if (payload.DeviceId is not null || payload.DisplayName is not null || payload.Jpeg is not null ||
                 (payload.ViewScreen && message.Type != MessageType.Approval))
            throw new InvalidDataException("Unexpected message payload.");
    }

    public static async Task WriteAsync(Stream stream, Envelope message, CancellationToken token)
    {
        Validate(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Frame exceeds size limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<Envelope> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size < 2 || size > MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
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
