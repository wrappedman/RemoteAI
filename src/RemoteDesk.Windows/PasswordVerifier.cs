using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace RemoteDesk.Windows;

public static class PasswordVerifier
{
    public static string Create(string password)
    {
        if (password.Length is < 16 or > 256) throw new ArgumentException("Use a unique password with at least 16 characters (maximum 256).");
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        byte[] bytes = [..salt, ..hash];
        try { return Convert.ToBase64String(Transform(bytes, true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(hash); }
    }
    public static bool Verify(string encrypted, string password)
    {
        if (password.Length is < 16 or > 256) return false;
        try
        {
            byte[] bytes = Transform(Convert.FromBase64String(encrypted), false);
            try
            {
                if (bytes.Length != 64) return false;
                byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, bytes.AsSpan(0, 32), 600000, HashAlgorithmName.SHA256, 32);
                try { return CryptographicOperations.FixedTimeEquals(hash, bytes.AsSpan(32)); }
                finally { CryptographicOperations.ZeroMemory(hash); }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return false; }
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length); var input = new BLOB { Size = bytes.Length, Data = memory };
            BLOB output;
            bool ok = protect ? CryptProtectData(ref input, "RemoteDesk password verifier", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try { byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); _ = LocalFree(output.Data); }
        }
        finally { for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(memory, i, 0); Marshal.FreeHGlobal(memory); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BLOB { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref BLOB input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out BLOB output);
    [DllImport("crypt32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref BLOB input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out BLOB output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
