using System.Buffers.Binary;
namespace RemoteDesk.Protocol;

public static class JpegFrame
{
    public const int MaxWidth = 1600, MaxHeight = 1000;
    // Read bounded JPEG headers before invoking the native image decoder. No huge image allocations.
    public static (int Width, int Height) Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 16 or > WireProtocol.MaxJpegBytes || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9)
            throw new InvalidDataException("Invalid JPEG frame.");
        int offset = 2;
        (int Width, int Height)? size = null;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset++] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            byte marker = bytes[offset++];
            if (marker == 0xda) return size ?? throw new InvalidDataException("Missing JPEG dimensions.");
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) throw new InvalidDataException("Unexpected JPEG marker.");
            if (offset + 2 > bytes.Length) throw new InvalidDataException("Truncated JPEG marker.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length) throw new InvalidDataException("Invalid JPEG segment.");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8 || bytes[offset + 2] != 8 || size is not null) throw new InvalidDataException("Unsupported JPEG format.");
                int height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                if (width is < 1 or > MaxWidth || height is < 1 or > MaxHeight) throw new InvalidDataException("JPEG dimensions exceed limits.");
                size = (width, height);
            }
            offset += length;
        }
        throw new InvalidDataException("Incomplete JPEG headers.");
    }
}
