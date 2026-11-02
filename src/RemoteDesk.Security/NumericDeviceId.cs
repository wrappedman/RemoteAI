using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Security;

public static class NumericDeviceId
{
    public static string FromFingerprint(string fingerprint) =>
        (BinaryPrimitives.ReadUInt64BigEndian(Convert.FromHexString(DeviceIdentity.NormalizeId(fingerprint))) % 1_000_000_000_000UL).ToString("D12", CultureInfo.InvariantCulture);
    public static string Normalize(string value)
    {
        var code = value.Replace(" ", "").Replace("-", "");
        if (code.Length != 12 || code.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Enter the destination's 12-digit RemoteDesk ID.");
        return code;
    }
    public static string Format(string code)
    {
        code = Normalize(code);
        return $"{code[..3]} {code[3..6]} {code[6..9]} {code[9..]}";
    }
    // Both peers derive this from the same TLS identities and fresh session UUID.
    public static string VerificationCode(Guid sessionId, string controllerId, string hostId)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes($"RemoteDesk.Pair.v2|{sessionId:N}|{controllerId}|{hostId}"));
        return (BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
