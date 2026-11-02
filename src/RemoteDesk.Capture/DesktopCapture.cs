using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteDesk.Protocol;

namespace RemoteDesk.Capture;

/// <summary>View-only primary display capture in the current user's interactive desktop. No elevated or Secure Desktop access.</summary>
public sealed class DesktopCapture : IDisposable
{
    private IntPtr _screen, _memory, _bitmap, _oldBitmap;
    private int _width, _height;
    public byte[] Capture(int maxWidth = 1440, int qualityLevel = 55)
    {
        int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
        if (width is < 1 or > 16384 || height is < 1 or > 8192 || (long)width * height > 70_000_000)
            throw new IOException("Unsupported display dimensions.");
        if (_bitmap == IntPtr.Zero || width != _width || height != _height) Initialize(width, height);
        if (!BitBlt(_memory, 0, 0, width, height, _screen, 0, 0, 0x40CC0020)) throw new Win32Exception();
        var source = Imaging.CreateBitmapSourceFromHBitmap(_bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        maxWidth = Math.Clamp(maxWidth, 640, 1920); qualityLevel = Math.Clamp(qualityLevel, 20, 85);
        double scale = Math.Min(1, Math.Min((double)maxWidth / width, 1200d / height));
        BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        scaled.Freeze();
        foreach (int quality in new[] { qualityLevel, 35, 20 }.Distinct())
        {
            using var output = new MemoryStream();
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(scaled)); encoder.Save(output);
            if (output.Length <= WireProtocol.MaxJpegBytes)
            {
                byte[] jpeg = output.ToArray(); JpegFrame.Validate(jpeg); return jpeg;
            }
        }
        throw new IOException("Screen frame exceeds the safe size limit.");
    }
    private void Initialize(int width, int height)
    {
        Dispose();
        _screen = GetDC(IntPtr.Zero);
        if (_screen == IntPtr.Zero) throw new Win32Exception();
        _memory = CreateCompatibleDC(_screen);
        _bitmap = CreateCompatibleBitmap(_screen, width, height);
        if (_memory == IntPtr.Zero || _bitmap == IntPtr.Zero) { Dispose(); throw new Win32Exception(); }
        _oldBitmap = SelectObject(_memory, _bitmap);
        if (_oldBitmap == IntPtr.Zero || _oldBitmap == new IntPtr(-1)) { Dispose(); throw new Win32Exception(); }
        _width = width; _height = height;
    }
    public void Dispose()
    {
        if (_memory != IntPtr.Zero && _oldBitmap != IntPtr.Zero && _oldBitmap != new IntPtr(-1)) _ = SelectObject(_memory, _oldBitmap);
        if (_bitmap != IntPtr.Zero) _ = DeleteObject(_bitmap);
        if (_memory != IntPtr.Zero) _ = DeleteDC(_memory);
        if (_screen != IntPtr.Zero) _ = ReleaseDC(IntPtr.Zero, _screen);
        _oldBitmap = _bitmap = _memory = _screen = IntPtr.Zero;
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
}
