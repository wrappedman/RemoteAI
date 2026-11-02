using System.ComponentModel;
using System.Runtime.InteropServices;
namespace RemoteDesk.Windows;

/// <summary>Session-scoped animation changes only after a separate Host permission. Always restore.</summary>
public sealed class DesktopEffects : IDisposable
{
    private bool _changed, _originalClient;
    private ANIMATIONINFO _original;
    public void SetDisabled(bool disable)
    {
        if (!disable) { Dispose(); return; }
        if (_changed) return;
        bool client = false; var animation = new ANIMATIONINFO { Size = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
        if (!SystemParametersBool(0x1042, 0, ref client, 0) || !SystemParametersAnimation(0x0048, animation.Size, ref animation, 0)) throw new Win32Exception();
        _originalClient = client; _original = animation; _changed = true;
        animation.Animate = 0;
        if (!SystemParametersValue(0x1043, 0, IntPtr.Zero, 2) || !SystemParametersAnimation(0x0049, animation.Size, ref animation, 2))
        { Dispose(); throw new Win32Exception(); }
    }
    public void Dispose()
    {
        if (!_changed) return; _changed = false;
        _ = SystemParametersValue(0x1043, 0, _originalClient ? new IntPtr(1) : IntPtr.Zero, 2);
        _ = SystemParametersAnimation(0x0049, _original.Size, ref _original, 2);
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersValue(uint action, uint value, IntPtr parameter, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct ANIMATIONINFO { public uint Size; public int Animate; }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersBool(uint action, uint value, [MarshalAs(UnmanagedType.Bool)] ref bool parameter, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersAnimation(uint action, uint value, ref ANIMATIONINFO parameter, uint flags);
}
