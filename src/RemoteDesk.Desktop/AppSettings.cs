using System.IO;
using System.Text.Json;
using RemoteDesk.Security;
using RemoteDesk.Files;
namespace RemoteDesk.Desktop;

public sealed class AppSettings
{
    public int Port { get; set; } = 45831;
    public bool AvailableOnStart { get; set; } = true;
    public bool AllowControl { get; set; } = true;
    public bool AllowFiles { get; set; } = true;
    public bool AllowEffects { get; set; }
    public bool AllowAdminRequests { get; set; }
    public bool AllowInputLock { get; set; }
    public bool PasswordAccess { get; set; }
    public bool PasswordControl { get; set; }
    public bool PasswordFiles { get; set; }
    public string? ProtectedPasswordVerifier { get; set; }
    public List<string> TrustedControllers { get; set; } = [];
    public string SharedFolder { get; set; } = "";
    public int DefaultFps { get; set; } = 20;
    public int DefaultWidth { get; set; } = 1440;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk");
    public static string TransferCache => Path.Combine(DataDirectory, "Transfers");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings-v3.json");
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath) && new FileInfo(SettingsPath).Length < 131072)
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
                settings.Validate(); return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { }
        return new();
    }
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || DefaultFps is < 1 or > 30 || DefaultWidth is < 640 or > 1920 || TrustedControllers is null || TrustedControllers.Count > 128)
            throw new ArgumentException("Invalid port, frame rate, width or trusted-key count.");
        TrustedControllers = TrustedControllers.Select(DeviceIdentity.NormalizeId).Distinct().ToList();
        SharedFolder ??= "";
        if (SharedFolder.Length > 1024 || (SharedFolder.Length > 0 && !Directory.Exists(SharedFolder))) throw new ArgumentException("Choose an existing shared folder or leave it empty.");
        if (SharedFolder.Length > 0)
        {
            string full = Path.GetFullPath(SharedFolder).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.GetPathRoot(full)!.TrimEnd(Path.DirectorySeparatorChar);
            string data = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.Equals(data, StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Share a dedicated subfolder, not an entire drive, profile ancestor or RemoteDesk settings/cache folder.");
            try { SharedFolderService.ValidateRoot(full); }
            catch (UnauthorizedAccessException) { throw new ArgumentException("The shared folder and its ancestors cannot be symbolic links/reparse points."); }
        }
        if (PasswordAccess && (string.IsNullOrEmpty(ProtectedPasswordVerifier) || TrustedControllers.Count == 0))
            throw new ArgumentException("Password access needs a 16+ character password and at least one explicitly trusted Controller (approve and check Trust first).");
    }
    public async Task SaveAsync()
    {
        Validate(); Directory.CreateDirectory(DataDirectory);
        string temporary = SettingsPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    }
}
