using System.Diagnostics;
using System.Security.Principal;
namespace RemoteDesk.Windows;

/// <summary>Normal interactive Windows elevation only. Never handles or bypasses the UAC prompt.</summary>
public static class ElevationLauncher
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public static ProcessStartInfo CreateStartInfo(string executable, string directory) => new(executable)
    { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory };
    public static bool Start(string executable, string directory) => Process.Start(CreateStartInfo(executable, directory)) is not null;
}
