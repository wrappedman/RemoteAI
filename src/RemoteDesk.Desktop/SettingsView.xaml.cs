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
        TrustedCount.Text = $"Trusted Controller keys: {_trusted.Count}. Pair manually and check Trust in the incoming approval to add a key.";
        NewPassword.Clear();
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
        SaveSettingsButton.IsEnabled = false; SettingsStatus.Text = "";
        try
        {
            if (!int.TryParse(PortSetting.Text, out int port)) throw new ArgumentException("Enter a TCP port between 1024 and 65535.");
            string? verifier = _removePassword ? null : _current.ProtectedPasswordVerifier;
            if (NewPassword.Password.Length > 0) { string password = NewPassword.Password; verifier = await Task.Run(() => PasswordVerifier.Create(password)); NewPassword.Clear(); }
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
            settings.Validate(); if (SaveRequested is not null) await SaveRequested(settings);
            Load(settings); SettingsStatus.Text = "Saved. Policies and shared folder updated; existing grants are unchanged. Only a port change restarts incoming availability.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { SettingsStatus.Text = ex.Message; }
        finally { SaveSettingsButton.IsEnabled = true; }
    }
}
