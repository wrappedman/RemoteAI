using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDesk.Security;
using RemoteDesk.Files;
namespace RemoteDesk.Desktop;

public sealed class AppSettings
{
    public int Port { get; set; } = 45831;
    public bool AvailableOnStart { get; set; } = true;
    public bool AllowControl { get; set; } = true;
    public bool AllowFiles { get; set; } = true;
    public bool AllowEffects { get; set; } = true;
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
    [JsonIgnore] public string? LoadWarning { get; private set; }
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static string SettingsPath => Path.Combine(DataDirectory, "settings-v3.json");
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath) && new FileInfo(SettingsPath).Length < 131072)
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
                // An unavailable USB/network share must not discard unrelated password/trust settings.
                try { settings.ValidateSharedFolder(); }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                { settings.SharedFolder = ""; settings.LoadWarning = "The saved shared folder is unavailable or unsafe. File serving is disabled until you choose another folder; password and trust settings were preserved."; }
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
        if (ProtectedPasswordVerifier?.Length > 8192) throw new ArgumentException("Invalid saved password verifier. Remove or replace the saved password.");
        if (PasswordAccess && string.IsNullOrEmpty(ProtectedPasswordVerifier))
            throw new ArgumentException("Set a unique password of at least 16 characters before enabling automatic approval. Pairing can be completed afterwards.");
        ValidateSharedFolder();
    }
    private void ValidateSharedFolder()
    {
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
    }
    public async Task SaveAsync()
    {
        Validate();
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        await SaveGate.WaitAsync();
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, SettingsPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            SaveGate.Release();
        }
    }
}
