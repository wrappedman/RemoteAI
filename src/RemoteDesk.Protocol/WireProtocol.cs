using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk.Protocol;

public enum MessageType { ConnectionRequest, Approval, Rejection, Ping, Pong, Disconnect, ScreenFrame, ScreenUnavailable, Input, Command, Reply }
public sealed record Payload(string? DeviceId = null, string? DisplayName = null, bool ViewScreen = false, byte[]? Jpeg = null, bool ControlInput = false, bool FileAccess = false, bool DesktopEffects = false, string? Password = null, InputEvent? Input = null, RemoteCommand? Command = null);
public sealed record Envelope(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid MessageId,
    [property: JsonRequired] Guid SessionId,
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] MessageType Type,
    [property: JsonRequired] Payload Payload);

public static class WireProtocol
{
    public const int Version = 4;
    public const int MaxFrameBytes = 768 * 1024;
    public const int MaxJpegBytes = 512 * 1024;
    public const int MaxMessagesPerSession = 1000000;
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
                payload.DisplayName.Any(char.IsControl) || payload.Jpeg is not null || payload.Input is not null || payload.Command is not null || payload.Password?.Length > 256)
                throw new InvalidDataException("Invalid connection request.");
        }
        else
        {
            if (payload.DeviceId is not null || payload.DisplayName is not null || payload.Password is not null ||
                (message.Type != MessageType.Approval && (payload.ViewScreen || payload.ControlInput || payload.FileAccess || payload.DesktopEffects)))
                throw new InvalidDataException("Unexpected capability or identity fields.");
            if (message.Type == MessageType.ScreenFrame)
            {
                if (payload.Jpeg is null) throw new InvalidDataException("Missing screen.");
                JpegFrame.Validate(payload.Jpeg);
            }
            else if (payload.Jpeg is not null) throw new InvalidDataException("Unexpected screen.");
            if (message.Type == MessageType.Input)
            {
                var i = payload.Input ?? throw new InvalidDataException("Missing input.");
                if (!Enum.IsDefined(i.Kind) || i.X is < 0 or > 65535 || i.Y is < 0 or > 65535 ||
                    i.Code is < 0 or > 254 || i.Delta is < -1200 or > 1200 ||
                    (i.Kind == InputKind.Key && i.Code == 0) || (i.Kind == InputKind.Button && i.Code is < 1 or > 5))
                    throw new InvalidDataException("Invalid input.");
            }
            else if (payload.Input is not null) throw new InvalidDataException("Unexpected input.");
            if (message.Type is MessageType.Command or MessageType.Reply)
            {
                var c = payload.Command ?? throw new InvalidDataException("Missing command.");
                if (c.Id == Guid.Empty || !Enum.IsDefined(c.Operation) || c.Path?.Length > 1024 || c.Data?.Length > 32768 ||
                    c.Offset is < 0 or > 8L * 1024 * 1024 * 1024 || c.Length is < 0 or > 8L * 1024 * 1024 * 1024 ||
                    c.Hash is not null && (c.Hash.Length != 64 || !c.Hash.All(Uri.IsHexDigit)) || c.Error?.Length > 256 || c.Entries?.Length > 256 ||
                    c.Entries?.Any(e => string.IsNullOrEmpty(e.Name) || e.Name.Length > 255 || e.Name.Any(char.IsControl) || e.Name.Contains('/') || e.Name.Contains('\\') || e.Name.Contains(':') || e.Size < 0) == true ||
                    (c.Operation == Operation.SetStream && (c.Fps is < 1 or > 30 || c.Width is < 640 or > 1920 || c.Quality is < 20 or > 85)))
                    throw new InvalidDataException("Invalid command.");
            }
            else if (payload.Command is not null) throw new InvalidDataException("Unexpected command.");
        }
    }

    public static async Task WriteAsync(Stream stream, Envelope message, CancellationToken token)
    {
        Validate(message);
        byte[] bytes;
        if (message.Type == MessageType.ScreenFrame)
        {
            bytes = new byte[44 + message.Payload.Jpeg!.Length];
            "RDV4"u8.CopyTo(bytes); message.SessionId.TryWriteBytes(bytes.AsSpan(4, 16));
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(20, 8), message.Sequence);
            message.MessageId.TryWriteBytes(bytes.AsSpan(28, 16));
            message.Payload.Jpeg.CopyTo(bytes, 44);
        }
        else bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
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
        if (bytes.AsSpan().StartsWith("RDV4"u8))
        {
            if (bytes.Length < 60) throw new InvalidDataException("Truncated media frame.");
            var media = new Envelope(Version, new Guid(bytes.AsSpan(28, 16)), new Guid(bytes.AsSpan(4, 16)),
                BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(20, 8)), MessageType.ScreenFrame, new(Jpeg: bytes[44..]));
            Validate(media); return media;
        }
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
