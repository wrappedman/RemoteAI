using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RemoteDesk.Agent;
using RemoteDesk.Core;
using RemoteDesk.Security;
using RemoteDesk.Transport;

namespace RemoteDesk.Desktop;

public sealed record SavedDevice(string Name, string Address, int Port, string Fingerprint);

public partial class MainWindow : Window
{
    private DeviceIdentity? _identity;
    private LanAgent? _agent;
    private SecureSession? _session;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource<bool>? _approval;
    private readonly SessionStateMachine _machine = new();
    private readonly ObservableCollection<AuditEntry> _audit = [];
    private List<SavedDevice> _devices = [];
    private bool _busy, _incoming, _closing, _canClose, _changingListener;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "devices.json");

    public MainWindow()
    {
        InitializeComponent();
        SearchBox.ToolTip = "Search saved devices";
        AuditList.ItemsSource = _audit;
    }
    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.Invoke(action);
    }
    private void Log(string message) => Ui(() =>
    {
        _audit.Insert(0, new AuditEntry(DateTimeOffset.Now, "Endpoint", message));
        while (_audit.Count > 100) _audit.RemoveAt(_audit.Count - 1);
    });
    private void SetState(SessionState state) => Ui(() =>
    {
        if (state == SessionState.Disconnected) ResetState();
        else
        {
            if (_machine.State == state) return;
            _machine.MoveTo(state);
            TopStatus.Text = state == SessionState.Connected ? "●  Encrypted LAN session active" : "●  " + state;
            StateText.Text = state switch { SessionState.WaitingForApproval => "Waiting for approval", _ => state.ToString() };
            ConnectButton.IsEnabled = false;
            DisconnectButton.Visibility = Visibility.Visible;
            Log($"Session state: {state}");
        }
    });
    private void ResetState()
    {
        if (_machine.State != SessionState.Disconnected)
        {
            if (_machine.State != SessionState.Disconnecting) _machine.MoveTo(SessionState.Disconnecting);
            _machine.MoveTo(SessionState.Disconnected);
        }
        _busy = false; _incoming = false;
        StateText.Text = "Disconnected";
        PeerText.Text = "No active peer";
        TopStatus.Text = _agent is null ? "●  Ready · listener off" : "●  Ready for attended requests";
        CipherText.Text = "Mutual TLS · pinned identity";
        ConnectButton.IsEnabled = _identity is not null && !_closing;
        DisconnectButton.Visibility = Visibility.Collapsed;
        ApprovalPanel.Visibility = Visibility.Collapsed;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ConnectButton.IsEnabled = false;
            _identity = await Task.Run(DeviceIdentity.LoadOrCreateWindows);
            if (_closing) { _identity.Dispose(); return; }
            IdentityBox.Text = _identity.DeviceId;
            MachineNameText.Text = Environment.MachineName;
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString());
            LocalAddressText.Text = string.Join("  ·  ", addresses.DefaultIfEmpty("No active IPv4 network"));
            FooterText.Text = $"RemoteDesk  /  {Environment.MachineName}  /  Phase 1";
            try
            {
                if (File.Exists(_settingsPath) && new FileInfo(_settingsPath).Length <= 131072)
                    _devices = JsonSerializer.Deserialize<List<SavedDevice>>(await File.ReadAllTextAsync(_settingsPath)) ?? [];
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
            { Log("Saved devices could not be loaded."); }
            FilterDevices();
            ConnectButton.IsEnabled = true;
            Log("User-scoped CNG device identity ready.");
            TopStatus.Text = "●  Ready · listener off";
            Log("Incoming connections are off. Enable attended requests on a trusted LAN.");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = "Device identity could not be initialized. Check your Windows user profile and key-store access.";
            Log("Identity initialization failed. No connections allowed.");
        }
    }
    private async void Listen_Changed(object sender, RoutedEventArgs e)
    {
        if (_changingListener || _closing) return;
        if (_identity is null)
        {
            _changingListener = true; ListenCheck.IsChecked = false; _changingListener = false;
            return;
        }
        ListenCheck.IsEnabled = false;
        try
        {
            if (ListenCheck.IsChecked == true)
            {
                var agent = new LanAgent(_identity)
                {
                    TryReserve = () => Dispatcher.Invoke(() =>
                    {
                        if (_busy || _closing) return false;
                        _busy = true; _incoming = true; return true;
                    }),
                    Approve = (request, token) => Dispatcher.InvokeAsync(() => AskApprovalAsync(request, token)).Task.Unwrap(),
                    StateChanged = state => Ui(() => { if (_incoming) SetState(state); }),
                    Established = session => Dispatcher.InvokeAsync(() => AttachSession(session, false)).Task,
                    Audit = Log
                };
                try { agent.Start(); _agent = agent; }
                catch { await agent.DisposeAsync(); throw; }
                ListenerBadge.Text = "● LISTENING";
                TopStatus.Text = "●  Ready for attended requests";
                Log("LAN listener started on TCP 45831.");
            }
            else
            {
                if (_agent is not null) { var agent = _agent; _agent = null; await agent.DisposeAsync(); }
                ListenerBadge.Text = "OFFLINE";
                TopStatus.Text = "●  Ready · listener off";
                Log("LAN listener stopped.");
            }
        }
        catch (SocketException)
        {
            _changingListener = true; ListenCheck.IsChecked = false; _changingListener = false;
            ErrorText.Text = "Cannot listen on port 45831. Another instance may be running, or Windows blocked the listener.";
            Log("Listener could not start.");
        }
        finally { ListenCheck.IsEnabled = true; }
    }
    private async Task<bool> AskApprovalAsync(ApprovalRequest request, CancellationToken token)
    {
        _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ApprovalDetails.Text = $"Name (unverified): {request.DisplayName}\nAddress: {request.Address}\nDevice fingerprint:\n{request.DeviceId}\nExpires in 45 seconds.";
        ApprovalPanel.Visibility = Visibility.Visible;
        Activate();
        Log("Incoming request needs local fingerprint verification.");
        try { return await _approval.Task.WaitAsync(token); }
        finally { _approval = null; ApprovalPanel.Visibility = Visibility.Collapsed; }
    }
    private void Approve_Click(object sender, RoutedEventArgs e) => _approval?.TrySetResult(true);
    private void Reject_Click(object sender, RoutedEventArgs e) => _approval?.TrySetResult(false);
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_identity is null || _busy) return;
        ErrorText.Text = "";
        try
        {
            var address = AddressBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(address) || Uri.CheckHostName(address) == UriHostNameType.Unknown)
                throw new ArgumentException("Enter a valid LAN IP address or hostname, without a scheme or port.");
            if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
                throw new ArgumentException("Port must be between 1 and 65535.");
            string pin = DeviceIdentity.NormalizeId(FingerprintBox.Text);
            if (pin == _identity.DeviceId) throw new ArgumentException("Choose another device, not this device's fingerprint.");
            _busy = true; _incoming = false;
            _operation?.Dispose(); _operation = new();
            string name = new(Environment.MachineName.Where(c => !char.IsControl(c)).Take(64).ToArray());
            var session = await SecureSession.ConnectAsync(address, port, pin, name, _identity, SetState, _operation.Token);
            if (_closing || _operation.IsCancellationRequested) { await session.DisposeAsync(); ResetState(); return; }
            AttachSession(session, true);
            session.Start();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or SocketException or AuthenticationException or UnauthorizedAccessException or OperationCanceledException)
        {
            ErrorText.Text = ex switch
            {
                ArgumentException => ex.Message,
                AuthenticationException => "TLS authentication failed. Verify the Host fingerprint and both devices' clocks. Do not bypass this check.",
                UnauthorizedAccessException => "The Host declined your request.",
                OperationCanceledException => "Connection cancelled or timed out. The Host must approve within 45 seconds.",
                _ => "Connection failed. Check the Host address, listener and Private-network firewall rule."
            };
            Log("Connection did not complete.");
            ResetState();
        }
    }
    private void AttachSession(SecureSession session, bool owned)
    {
        _session = session;
        PeerText.Text = $"Peer: {session.PeerName} · {session.PeerId}";
        CipherText.Text = session.Cipher;
        Log("Mutual TLS session established; desktop and input remain disabled.");
        _ = ObserveSessionAsync(session, owned);
    }
    private async Task ObserveSessionAsync(SecureSession session, bool owned)
    {
        await session.Completion;
        Ui(() =>
        {
            Log(session.CloseReason);
            if (ReferenceEquals(_session, session)) { _session = null; ResetState(); }
        });
        if (owned) await session.DisposeAsync();
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel(); _approval?.TrySetResult(false);
        if (_session is not null) { SetState(SessionState.Disconnecting); await _session.DisconnectAsync(); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (_identity is not null) { Clipboard.SetText(_identity.DeviceId); Log("Public device ID copied."); } }
        catch (System.Runtime.InteropServices.COMException) { ErrorText.Text = "Clipboard is busy. Select and copy the device ID manually."; }
    }
    private void FilterDevices()
    {
        if (DeviceList is null) return;
        string search = SearchBox.Text.Trim();
        var filtered = _devices.Where(d => d.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || d.Address.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        DeviceList.ItemsSource = filtered;
        EmptyDevices.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterDevices();
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pin = DeviceIdentity.NormalizeId(FingerprintBox.Text);
            var address = AddressBox.Text.Trim();
            if (Uri.CheckHostName(address) == UriHostNameType.Unknown || !int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
                throw new ArgumentException("Enter a valid target address, port and fingerprint before saving.");
            _devices.RemoveAll(d => d.Fingerprint == pin);
            _devices.Insert(0, new(address, address, port, pin));
            if (_devices.Count > 100) _devices.RemoveAt(_devices.Count - 1);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(_devices));
            FilterDevices(); Log("Target saved locally. Approval is still required on every connection.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { ErrorText.Text = ex is ArgumentException ? ex.Message : "Saved devices could not be written to your user profile."; }
    }
    private void Device_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not SavedDevice device) return;
        AddressBox.Text = device.Address; PortBox.Text = device.Port.ToString(); FingerprintBox.Text = device.Fingerprint;
    }
    private void Home_Click(object sender, RoutedEventArgs e) => AddressBox.Focus();
    private void Guide_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1. Run RemoteDesk on both Windows devices.\n2. On the Host, enable attended requests. Allow TCP 45831 only on a trusted Private network.\n3. Share the Host address and full device ID using a trusted channel.\n4. The Controller enters them and selects Connect securely.\n5. On the Host, compare the Controller's full fingerprint and approve or reject.\n\nPhase 1 exchanges protocol and heartbeat messages only. Screen viewing, keyboard, mouse, relay and unattended access are NOT implemented.\n\nKeys are held in your Windows user-scoped CNG store. No administrator access is requested. See docs/Security.md for limitations.",
        "RemoteDesk · Security & setup", MessageBoxButton.OK, MessageBoxImage.Information);
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _operation?.Cancel(); _approval?.TrySetResult(false);
        if (_session is not null) await _session.DisconnectAsync();
        if (_agent is not null) await _agent.DisposeAsync();
        _identity?.Dispose(); _operation?.Dispose();
        _canClose = true; Close();
    }
}
