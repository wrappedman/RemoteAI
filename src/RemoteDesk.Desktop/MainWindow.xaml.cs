using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteDesk.Agent;
using RemoteDesk.Core;
using RemoteDesk.Discovery;
using RemoteDesk.Protocol;
using RemoteDesk.Security;
using RemoteDesk.Transport;
using RemoteDesk.Windows;
using Microsoft.Win32;
namespace RemoteDesk.Desktop;

public sealed record SavedDevice(string Name, string Code, string? Fingerprint = null);
public partial class MainWindow : Window
{
    private sealed record WorkspaceSession(SecureSession Session, SessionView View, TabItem Tab, HostSessionRuntime? Host, bool Owned);
    private readonly Dictionary<SecureSession, WorkspaceSession> _sessions = [];
    public IReadOnlyList<SessionView> ActiveViews => _sessions.Values.Select(s => s.View).ToList();
    private DeviceIdentity? _identity;
    private LanAgent? _agent;
    private LanDiscovery? _discovery;
    private AppSettings _settings = new();
    private readonly ObservableCollection<AuditEntry> _audit = [];
    private List<SavedDevice> _devices = [];
    private CancellationTokenSource? _connection;
    private TaskCompletionSource<SessionGrant?>? _approval;
    private ApprovalRequest? _approvalRequest;
    private bool _connecting, _closing, _canClose, _listenerChanging, _fullScreen;
    private int _passwordFailures;
    private long _passwordBlockedUntil;
    private WindowState _oldWindowState;
    private bool _restartPending;
    private readonly string _devicesPath = Path.Combine(AppSettings.DataDirectory, "devices-v2.json");
    public MainWindow()
    {
        InitializeComponent(); AuditList.ItemsSource = _audit;
        SettingsPage.SaveRequested = SaveSettingsAsync;
    }
    private void Ui(Action action) { if (Dispatcher.CheckAccess()) action(); else Dispatcher.Invoke(action); }
    private void Post(Action action) => _ = Dispatcher.BeginInvoke(action);
    private void Log(string detail) => Ui(() => { _audit.Insert(0, new(DateTimeOffset.Now, "Endpoint", detail)); while (_audit.Count > 100) _audit.RemoveAt(_audit.Count - 1); });
    private static string DisplayName() => new(Environment.MachineName.Where(c => !char.IsControl(c)).Take(64).ToArray());
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            _settings = AppSettings.Load();
            _identity = await Task.Run(DeviceIdentity.LoadOrCreateWindows);
            if (_closing) { _identity.Dispose(); return; }
            IdentityBox.Text = NumericDeviceId.Format(NumericDeviceId.FromFingerprint(_identity.DeviceId));
            FullFingerprintBox.Text = _identity.DeviceId; MachineNameText.Text = Environment.MachineName;
            try
            {
                if (File.Exists(_devicesPath) && new FileInfo(_devicesPath).Length <= 131072)
                    _devices = (JsonSerializer.Deserialize<List<SavedDevice>>(await File.ReadAllTextAsync(_devicesPath)) ?? [])
                        .Where(d => d.Name is { Length: > 0 and <= 64 } && d.Code is { Length: 12 } && d.Code.All(char.IsAsciiDigit)).Take(100).ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log("Saved targets could not be loaded."); }
            FilterDevices(); SettingsPage.Load(_settings); ConnectButton.IsEnabled = true;
            ListenCheck.IsChecked = _settings.AvailableOnStart;
            if (!_settings.AvailableOnStart) ListenerBadge.Text = "Availability off";
            Log("Workspace ready. Each device gets an independent session tab.");
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        { ErrorText.Text = "Cannot initialize the Windows device identity or user settings."; }
    }
    private async Task StartAvailabilityAsync()
    {
        if (_identity is null || _agent is not null) return;
        var agent = new LanAgent(_identity)
        {
            TryReserve = () => !_closing,
            Authorize = AuthorizeAsync,
            Established = session => Dispatcher.InvokeAsync(() => AttachSession(session, false)).Task,
            Audit = Log
        };
        try { agent.Start(_settings.Port); _agent = agent; }
        catch { await agent.DisposeAsync(); throw; }
        try { _discovery = new LanDiscovery(_identity, agent.Port, DisplayName()); _discovery.Start(); ListenerBadge.Text = "● Available on this LAN"; }
        catch (SocketException) { ListenerBadge.Text = "Manual address only"; ErrorText.Text = "UDP discovery 45832 is busy. Close duplicate instances or use manual setup."; }
        Log($"Incoming TLS listener started on configured port {agent.Port}.");
    }
    private async Task StopAvailabilityAsync()
    {
        if (_discovery is not null) { var d = _discovery; _discovery = null; await d.DisposeAsync(); }
        if (_agent is not null) { var a = _agent; _agent = null; await a.DisposeAsync(); }
        ListenerBadge.Text = "OFFLINE";
    }
    private async void Listen_Changed(object sender, RoutedEventArgs e)
    {
        if (_listenerChanging || _closing || _identity is null) return;
        _listenerChanging = true; ListenCheck.IsEnabled = false;
        try { if (ListenCheck.IsChecked == true) await StartAvailabilityAsync(); else await StopAvailabilityAsync(); }
        catch (SocketException) { ListenCheck.IsChecked = false; ErrorText.Text = "Cannot bind the chosen TCP port. Close duplicate instances or choose another port in Settings."; }
        finally { ListenCheck.IsEnabled = true; _listenerChanging = false; }
    }
    private async Task<SessionGrant?> AuthorizeAsync(ApprovalRequest request, CancellationToken token)
    {
        // Password access is deliberately restricted to previously, explicitly trusted full keys.
        if (_settings.PasswordAccess && !string.IsNullOrEmpty(request.Password))
        {
            if (Environment.TickCount64 < Interlocked.Read(ref _passwordBlockedUntil)) return null;
            bool trusted = _settings.TrustedControllers.Contains(request.DeviceId, StringComparer.Ordinal);
            bool valid = trusted && _settings.ProtectedPasswordVerifier is not null &&
                await Task.Run(() => PasswordVerifier.Verify(_settings.ProtectedPasswordVerifier, request.Password), token);
            if (!valid)
            {
                if (Interlocked.Increment(ref _passwordFailures) >= 3) { Interlocked.Exchange(ref _passwordBlockedUntil, Environment.TickCount64 + 60000); Interlocked.Exchange(ref _passwordFailures, 0); }
                Log("Automatic approval refused (untrusted key, invalid password or cooldown)."); return null;
            }
            Interlocked.Exchange(ref _passwordFailures, 0);
            Log("Password-authorized, explicitly trusted Controller connected while the Host app is visible.");
            return new(request.ViewScreen, request.ControlInput && _settings.AllowControl && _settings.PasswordControl,
                request.FileAccess && _settings.AllowFiles && _settings.PasswordFiles, false);
        }
        return await Dispatcher.InvokeAsync(() => PromptAsync(request, token)).Task.Unwrap();
    }
    private async Task<SessionGrant?> PromptAsync(ApprovalRequest request, CancellationToken token)
    {
        _approval = new(TaskCreationOptions.RunContinuationsAsynchronously); _approvalRequest = request;
        ApprovalDetails.Text = $"Device {NumericDeviceId.Format(NumericDeviceId.FromFingerprint(request.DeviceId))}\nName (unverified): {request.DisplayName}\nSession reference: {request.VerificationCode}\nFull Controller key:\n{request.DeviceId}\nExpires in 120 seconds.";
        RefreshApprovalPermissions();
        GrantControlCheck.IsChecked = GrantFilesCheck.IsChecked = GrantEffectsCheck.IsChecked = TrustControllerCheck.IsChecked = false;
        ApprovalPanel.Visibility = Visibility.Visible; Workspace.SelectedItem = ConnectionCenterTab; Activate();
        Post(() => ApprovalPanel.BringIntoView());
        try { return await _approval.Task.WaitAsync(token); }
        finally { _approval = null; _approvalRequest = null; ApprovalPanel.Visibility = Visibility.Collapsed; }
    }
    private void RefreshApprovalPermissions()
    {
        if (_approvalRequest is not { } request) return;
        GrantControlCheck.IsEnabled = request.ControlInput && _settings.AllowControl;
        GrantFilesCheck.IsEnabled = request.FileAccess && _settings.AllowFiles;
        GrantEffectsCheck.IsEnabled = request.DesktopEffects && _settings.AllowEffects;
        var hints = new List<string>();
        void Explain(CheckBox box, bool requested, bool policy, string name)
        {
            string reason = !requested ? $"{name} was not requested. The Controller must select it before reconnecting."
                : !policy ? $"{name} is disabled by your local policy. Open Review permission policies, enable it and Save; return here to approve." : $"You may grant {name} to this session only.";
            box.ToolTip = reason;
            if (!box.IsEnabled) { box.IsChecked = false; hints.Add(reason); }
        }
        Explain(GrantControlCheck, request.ControlInput, _settings.AllowControl, "Control");
        Explain(GrantFilesCheck, request.FileAccess, _settings.AllowFiles, "Files");
        Explain(GrantEffectsCheck, request.DesktopEffects, _settings.AllowEffects, "Animations");
        ApprovalPolicyHint.Text = hints.Count == 0 ? "Choose what to grant. Unchecked permissions remain denied; file exchange does not expose an unselected folder." : string.Join("\n", hints);
        ApprovalFolderText.Text = string.IsNullOrEmpty(_settings.SharedFolder) ? "No local folder selected. You can still grant file exchange and select Share my folder in the session later." : "Your shared folder: " + _settings.SharedFolder;
    }
    private async void ApprovalFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder this Controller may browse after Files approval" };
        if (dialog.ShowDialog(this) != true) return;
        string old = _settings.SharedFolder;
        try { _settings.SharedFolder = dialog.FolderName; await _settings.SaveAsync(); RefreshApprovalPermissions(); SettingsPage.Load(_settings); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { _settings.SharedFolder = old; ApprovalPolicyHint.Text = ex.Message; }
    }
    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_approvalRequest is not { } request || _approval is null) return;
        var grant = new SessionGrant(request.ViewScreen, GrantControlCheck.IsEnabled && GrantControlCheck.IsChecked == true,
            GrantFilesCheck.IsEnabled && GrantFilesCheck.IsChecked == true, GrantEffectsCheck.IsEnabled && GrantEffectsCheck.IsChecked == true);
        var promise = _approval;
        if (TrustControllerCheck.IsChecked == true && !_settings.TrustedControllers.Contains(request.DeviceId))
        {
            if (_settings.TrustedControllers.Count >= 128) { ErrorText.Text = "Trusted Controller limit reached. Forget keys in Settings."; return; }
            _settings.TrustedControllers.Add(request.DeviceId);
            try { await _settings.SaveAsync(); SettingsPage.Load(_settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { _settings.TrustedControllers.Remove(request.DeviceId); ErrorText.Text = "Could not persist trust. This session can still use manual consent."; }
        }
        promise.TrySetResult(grant);
    }
    private void Reject_Click(object sender, RoutedEventArgs e) => _approval?.TrySetResult(null);
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_identity is null || _connecting || _closing) return;
        if (_sessions.Values.Count(s => s.Owned) >= 8) { ErrorText.Text = "Maximum 8 outgoing session tabs. Disconnect a tab first."; return; }
        _connecting = true; ConnectButton.IsEnabled = false; CancelConnectButton.Visibility = Visibility.Visible; ErrorText.Text = "";
        _connection?.Dispose(); _connection = new();
        try
        {
            string address, pin; int port; string password = ConnectionPassword.Password;
            if (ManualCheck.IsChecked == true)
            {
                address = AddressBox.Text.Trim(); if (Uri.CheckHostName(address) == UriHostNameType.Unknown) throw new ArgumentException("Enter a valid LAN address.");
                if (!int.TryParse(PortBox.Text, out port) || port is < 1 or > 65535) throw new ArgumentException("Invalid Host port.");
                pin = DeviceIdentity.NormalizeId(FingerprintBox.Text);
            }
            else
            {
                string code = NumericDeviceId.Normalize(TargetIdBox.Text);
                if (code == NumericDeviceId.FromFingerprint(_identity.DeviceId)) throw new ArgumentException("Choose another device's ID.");
                ConnectionStatus.Text = "Finding device on LAN…";
                var known = _devices.FirstOrDefault(d => d.Code == code && d.Fingerprint is not null);
                if (!string.IsNullOrEmpty(password) && known is null) throw new ArgumentException("Pair manually before sending a password. A previously pinned Host key is required for automatic access.");
                var target = await LanDiscovery.ResolveAsync(code, _connection.Token);
                if (known is not null && known.Fingerprint != target.Fingerprint) throw new AuthenticationException("Saved Host key changed.");
                address = target.Address.ToString(); pin = target.Fingerprint; port = target.Port;
            }
            if (pin == _identity.DeviceId) throw new ArgumentException("Choose another device, not this profile's identity.");
            var requested = new SessionGrant(true, RequestControlCheck.IsChecked == true, RequestFilesCheck.IsChecked == true, RequestEffectsCheck.IsChecked == true);
            var session = await SecureSession.ConnectAsync(address, port, pin, DisplayName(), _identity,
                state => Ui(() => ConnectionStatus.Text = state.ToString()), _connection.Token, requested: requested, password: string.IsNullOrEmpty(password) ? null : password,
                verificationCode: code => Ui(() => { PairingText.Text = $"Session reference {code}. Compare trusted full fingerprints on an untrusted network."; PairingPanel.Visibility = Visibility.Visible; }));
            if (_closing || _connection.IsCancellationRequested) { await session.DisposeAsync(); return; }
            AttachSession(session, true); session.Start();
            try { await SaveTargetAsync(NumericDeviceId.FromFingerprint(pin), pin); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Connected, but target pin could not be saved."); }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or SocketException or AuthenticationException or UnauthorizedAccessException or OperationCanceledException)
        {
            ErrorText.Text = ex switch
            {
                ArgumentException or InvalidDataException => ex.Message,
                AuthenticationException => "Host identity failed or a saved key changed. Never bypass a changed pin; verify its full fingerprint independently.",
                UnauthorizedAccessException => "Host denied access (consent/password/trusted-key policy).",
                OperationCanceledException => "Connection cancelled or approval timed out.",
                IOException => ex.Message.StartsWith("Device not found", StringComparison.Ordinal) ? ex.Message : "Connection failed. Update both endpoints to v0.3.1 and check the configured TCP / UDP discovery firewall rules.",
                _ => "Cannot reach the Host. Check availability, port and Private-network firewall."
            };
            ConnectionStatus.Text = "Not connected";
        }
        finally
        {
            ConnectionPassword.Clear(); _connecting = false; ConnectButton.IsEnabled = !_closing;
            CancelConnectButton.Visibility = Visibility.Collapsed; PairingPanel.Visibility = Visibility.Collapsed;
        }
    }
    private void CancelConnect_Click(object sender, RoutedEventArgs e) => _connection?.Cancel();
    private void AttachSession(SecureSession session, bool owned)
    {
        var view = new SessionView(session, _settings.DefaultFps, _settings.DefaultWidth);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = (owned ? "▣ " : "↙ ") + NumericDeviceId.Format(NumericDeviceId.FromFingerprint(session.PeerId)), VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(10, 0, 0, 0), Background = Brushes.Transparent, ToolTip = "Disconnect this device" };
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = view };
        var host = new HostSessionRuntime(session, _settings, operation => ConfirmSensitiveAsync(session, operation), status => view.SetStatus(status));
        view.ActionReported = Log;
        view.ShareLocalFolder = folder => { host.ShareFolder(folder); return Task.CompletedTask; };
        view.StopLocalSharing = host.StopSharing;
        view.ConfiguredFolder = () => _settings.SharedFolder;
        view.SetLocalFolder(host.SharedFolder);
        var context = new WorkspaceSession(session, view, tab, host, owned); _sessions.Add(session, context);
        view.FullScreen = SetFullScreen;
        view.CloseRequested = () => session.DisconnectAsync();
        close.Click += async (_, _) => await session.DisconnectAsync();
        Workspace.Items.Insert(1, tab); Workspace.SelectedItem = tab; Post(() => tab.BringIntoView());
        UpdateSessionCount(); Log(owned ? "Controller session opened in an independent tab." : "Host session authorized; granted capabilities are visible in its tab.");
        _ = ObserveAsync(context);
    }
    private async Task ObserveAsync(WorkspaceSession context)
    {
        await context.Session.Completion;
        try
        {
            await context.View.DisposeAsync(); context.Host?.Dispose();
            if (context.Owned) await context.Session.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { Log("Session cleanup completed after cancellation."); }
        _sessions.Remove(context.Session); Workspace.Items.Remove(context.Tab); UpdateSessionCount();
        if (_sessions.Count == 0) SetFullScreen(false);
        ConnectionStatus.Text = context.Session.CloseReason; Log(context.Session.CloseReason);
    }
    private void UpdateSessionCount() => SessionCountText.Text = $"{_sessions.Values.Count(s => s.Owned)} outgoing · {_sessions.Values.Count(s => !s.Owned)} incoming";
    private async Task<bool> ConfirmSensitiveAsync(SecureSession session, Operation operation)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            if (_closing || session.Completion.IsCompleted) return false;
            if (operation == Operation.RequestInputLock)
                return MessageBox.Show(this, "The Controller requests blocking your PHYSICAL MOUSE AND KEYBOARD for at most 30 seconds. Windows may require elevation. Ctrl+Alt+Del releases the block immediately; disconnect/process exit also releases it.\n\nAllow this one temporary block?", "RemoteDesk · local input consent", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (_restartPending) return false;
            if (ElevationLauncher.IsElevated()) { ErrorText.Text = "RemoteDesk is already running as administrator."; return false; }
            bool accepted = MessageBox.Show(this, "The Controller requests restarting RemoteDesk as administrator. All of this app's sessions will close. You must approve the NORMAL WINDOWS UAC prompt yourself. A different Windows account can have a different device ID.\n\nRestart with UAC now?", "RemoteDesk · admin restart", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            accepted &= !session.Completion.IsCompleted;
            if (accepted) { _restartPending = true; _ = RestartElevatedAsync(session); } return accepted;
        });
    }
    private async Task RestartElevatedAsync(SecureSession session)
    {
        bool wasAvailable = ListenCheck.IsChecked == true;
        await Task.Delay(1500); // Let the authenticated reply leave before closing the Host channel.
        try
        {
            if (_closing || session.Completion.IsCompleted) return;
            await StopAvailabilityAsync();
            string executable = Environment.ProcessPath ?? throw new IOException("Cannot find the executable.");
            bool started = await Task.Run(() => ElevationLauncher.Start(executable, AppContext.BaseDirectory));
            if (!started) throw new IOException("Windows did not start an elevated process.");
            Close();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = "UAC restart cancelled or failed. No elevation was bypassed. Reconnect after incoming availability is restored.";
            Log("Windows UAC restart cancelled or failed; no elevation bypass attempted.");
            if (wasAvailable && !_closing)
            {
                try { await StartAvailabilityAsync(); }
                catch (SocketException) { ListenCheck.IsChecked = false; ErrorText.Text = "UAC was not completed; the listening port is busy. Change the port or close duplicate instances."; }
            }
        }
        finally { _restartPending = false; }
    }
    private async Task SaveSettingsAsync(AppSettings settings)
    {
        bool available = ListenCheck.IsChecked == true;
        bool restart = settings.Port != _settings.Port;
        _listenerChanging = true; ListenCheck.IsEnabled = false;
        try
        {
            await settings.SaveAsync();
            if (restart) await StopAvailabilityAsync();
            _settings = settings;
            foreach (var context in _sessions.Values.ToArray())
            {
                context.Host?.UpdateSettings(settings); context.View.SetLocalFolder(context.Host?.SharedFolder);
            }
            RefreshApprovalPermissions();
            if (restart && available) await StartAvailabilityAsync();
        }
        catch (SocketException) { ListenCheck.IsChecked = false; throw new IOException("Settings saved, but the new TCP port is busy. Choose another or close duplicate instances."); }
        finally { ListenCheck.IsEnabled = true; _listenerChanging = false; }
    }
    public void SetFullScreen(bool full)
    {
        if (_fullScreen == full) return; _fullScreen = full;
        if (full) { _oldWindowState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; RailColumn.Width = new GridLength(0); }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = _oldWindowState; RailColumn.Width = new GridLength(218); }
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && Workspace.SelectedItem is TabItem { Content: SessionView }) { e.Handled = true; SetFullScreen(!_fullScreen); }
        if (e.Key == Key.Escape && _fullScreen) { e.Handled = true; SetFullScreen(false); }
    }
    private void Workspace_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Workspace.SelectedItem is TabItem { Content: not SessionView }) SetFullScreen(false); }
    private void Home_Click(object sender, RoutedEventArgs e) { Workspace.SelectedItem = ConnectionCenterTab; TargetIdBox.Focus(); }
    private void Settings_Click(object sender, RoutedEventArgs e) { SettingsPage.Load(_settings); SettingsTab.Visibility = Visibility.Visible; Workspace.SelectedItem = SettingsTab; }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (_identity is not null) Clipboard.SetText(NumericDeviceId.FromFingerprint(_identity.DeviceId)); }
        catch (System.Runtime.InteropServices.COMException) { ErrorText.Text = "Clipboard is busy; copy the displayed number manually."; }
    }
    private void FilterDevices()
    {
        if (DeviceList is null) return;
        string query = SearchBox.Text.Trim().Replace(" ", ""); DeviceList.ItemsSource = _devices.Where(d => d.Code.Contains(query, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterDevices();
    private async Task SaveTargetAsync(string code, string? pin)
    {
        var old = _devices.FirstOrDefault(d => d.Code == code); _devices.RemoveAll(d => d.Code == code);
        _devices.Insert(0, new("Device " + NumericDeviceId.Format(code), code, pin ?? old?.Fingerprint));
        if (_devices.Count > 100) _devices.RemoveAt(100); Directory.CreateDirectory(AppSettings.DataDirectory);
        await File.WriteAllTextAsync(_devicesPath, JsonSerializer.Serialize(_devices)); FilterDevices();
    }
    private async void SaveTarget_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveTargetAsync(NumericDeviceId.Normalize(TargetIdBox.Text), null); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { ErrorText.Text = "Enter a valid ID; saved targets must be writable in your user profile."; }
    }
    private void Device_Selected(object sender, SelectionChangedEventArgs e) { if (DeviceList.SelectedItem is SavedDevice device) { TargetIdBox.Text = NumericDeviceId.Format(device.Code); ManualCheck.IsChecked = false; Workspace.SelectedItem = ConnectionCenterTab; } }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return; e.Cancel = true; if (_closing) return; _closing = true;
        _connection?.Cancel(); _approval?.TrySetResult(null);
        foreach (var context in _sessions.Values.ToArray()) await context.Session.DisconnectAsync();
        await StopAvailabilityAsync();
        // The observers quiesce decoder/input tasks before their tabs are removed.
        while (_sessions.Count > 0) await Task.Delay(20);
        _identity?.Dispose(); _connection?.Dispose(); _canClose = true; _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
