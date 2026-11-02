using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RemoteDesk.Windows;
namespace RemoteDesk.Desktop;
public partial class SettingsView : UserControl
{
    public Func<AppSettings, Task>? SaveRequested { get; set; }
    private AppSettings _current = new(); private List<string> _trusted = []; private bool _removePassword;
    public SettingsView() => InitializeComponent();
    public void Load(AppSettings settings)
    {
        _current = settings; _trusted = settings.TrustedControllers.ToList(); _removePassword = false;
        PortSetting.Text = settings.Port.ToString(); AvailableSetting.IsChecked = settings.AvailableOnStart;
        ControlSetting.IsChecked = settings.AllowControl; FilesSetting.IsChecked = settings.AllowFiles; EffectsSetting.IsChecked = settings.AllowEffects;
        AdminSetting.IsChecked = settings.AllowAdminRequests; LockSetting.IsChecked = settings.AllowInputLock;
        FolderSetting.Text = settings.SharedFolder; PasswordSetting.IsChecked = settings.PasswordAccess;
        PasswordControlSetting.IsChecked = settings.PasswordControl; PasswordFilesSetting.IsChecked = settings.PasswordFiles;
        FpsSetting.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 5, 15, 20, 24, 30 }, settings.DefaultFps));
        WidthSetting.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 960, 1440, 1920 }, settings.DefaultWidth));
        RefreshTrustText(); NewPassword.Clear(); PasswordFeedback.Text = ""; RefreshPasswordState(settings);
    }
    private void RefreshTrustText() => TrustedCount.Text = $"Trusted Controller keys: {_trusted.Count}. Pair manually and check Trust in the incoming approval to add a key.";
    private void RefreshPasswordState(AppSettings settings)
    {
        bool usable = PasswordVerifier.IsUsable(settings.ProtectedPasswordVerifier);
        PasswordState.Text = string.IsNullOrEmpty(settings.ProtectedPasswordVerifier) ? "Saved state: no password configured."
            : !usable ? "Saved password cannot be unlocked by this Windows account. Set a new password here. Automatic access fails closed."
            : !settings.PasswordAccess ? "Saved state: password configured; automatic approval is OFF."
            : _trusted.Count == 0 ? "Saved state: password configured; waiting for a trusted Controller. Complete the one-time pairing below."
            : $"Saved state: automatic approval ready for {_trusted.Count} trusted Controller(s). No plaintext password is saved.";
    }
    public void TrustedControllerAdded(string key)
    {
        // Do not reload the form: pairing must not erase an unsaved password draft.
        if (!_trusted.Contains(key)) _trusted.Add(key);
        RefreshTrustText(); RefreshPasswordState(_current);
    }
    public void RefreshSharedFolder(string folder) => FolderSetting.Text = folder;
    private void NewPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordEditHint is null) return;
        int length = NewPassword.Password.Length;
        PasswordEditHint.Text = length == 0 ? "Leave empty to keep a saved password. Changes take effect only after Save settings."
            : length < 16 ? $"Not saved: {length} characters entered; at least 16 required."
            : "New password entered; use Save settings to apply it. It is cleared only after a successful save.";
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the only folder approved peers may access" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderSetting.Text = dialog.FolderName;
    }
    private void Cache_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(AppSettings.TransferCache); Process.Start(new ProcessStartInfo(AppSettings.TransferCache) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { SettingsStatus.Text = "Cannot open transfer cache."; }
    }
    private void Forget_Click(object sender, RoutedEventArgs e) { _trusted.Clear(); PasswordSetting.IsChecked = false; TrustedCount.Text = "Trusted keys will be cleared when you save."; }
    private void RemovePassword_Click(object sender, RoutedEventArgs e) { _removePassword = true; PasswordSetting.IsChecked = false; NewPassword.Clear(); SettingsStatus.Text = "The verifier will be removed when you save."; }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false; SaveSettingsButton.IsEnabled = false; SettingsStatus.Text = ""; PasswordFeedback.Text = "";
        try
        {
            if (!int.TryParse(PortSetting.Text, out int port)) throw new ArgumentException("Enter a TCP port between 1024 and 65535.");
            string? verifier = _removePassword ? null : _current.ProtectedPasswordVerifier;
            if (NewPassword.Password.Length > 0) { string password = NewPassword.Password; verifier = await Task.Run(() => PasswordVerifier.Create(password)); }
            var settings = new AppSettings
            {
                Port = port, AvailableOnStart = AvailableSetting.IsChecked == true, AllowControl = ControlSetting.IsChecked == true,
                AllowFiles = FilesSetting.IsChecked == true, AllowEffects = EffectsSetting.IsChecked == true,
                AllowAdminRequests = AdminSetting.IsChecked == true, AllowInputLock = LockSetting.IsChecked == true,
                SharedFolder = FolderSetting.Text.Trim(), PasswordAccess = PasswordSetting.IsChecked == true,
                PasswordControl = PasswordControlSetting.IsChecked == true, PasswordFiles = PasswordFilesSetting.IsChecked == true,
                ProtectedPasswordVerifier = verifier, TrustedControllers = _trusted.ToList(),
                DefaultFps = int.Parse(((ComboBoxItem)FpsSetting.SelectedItem).Content.ToString()!),
                DefaultWidth = int.Parse(((ComboBoxItem)WidthSetting.SelectedItem).Content.ToString()!)
            };
            settings.Validate();
            if (SaveRequested is null) throw new InvalidOperationException("Settings storage is not available.");
            await SaveRequested(settings);
            Load(settings); SettingsStatus.Text = "Saved. Policies and shared folder updated; existing grants are unchanged. Only a port change restarts incoming availability.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        { SettingsStatus.Text = "Not saved: " + ex.Message; PasswordFeedback.Text = SettingsStatus.Text; }
        finally { SaveSettingsButton.IsEnabled = true; IsEnabled = true; }
    }
}
