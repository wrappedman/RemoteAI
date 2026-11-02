using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesk.Agent;
using RemoteDesk.Capture;
using RemoteDesk.Core;
using RemoteDesk.Discovery;
using RemoteDesk.Security;
using RemoteDesk.Transport;

namespace RemoteDesk.Desktop;

public sealed record SavedDevice(string Name, string Code, string? Fingerprint = null);

public partial class MainWindow : Window
{
    private DeviceIdentity? _identity;
    private LanAgent? _agent;
    private LanDiscovery? _discovery;
    private volatile SecureSession? _session;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource<bool>? _approval;
    private readonly SessionStateMachine _machine = new();
    private readonly ObservableCollection<AuditEntry> _audit = [];
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private List<SavedDevice> _devices = [];
    private byte[]? _latestFrame;
    private DateTimeOffset _lastFrame;
    private long _receivedFrames;
    private bool _busy, _incoming, _closing, _canClose, _changingListener;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "devices-v2.json");

    public MainWindow()
    {
        InitializeComponent();
        AuditList.ItemsSource = _audit;
        _renderTimer.Tick += RenderFrame;
    }
    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action(); else Dispatcher.Invoke(action);
    }
    private void Log(string message) => Ui(() =>
    {
        _audit.Insert(0, new(DateTimeOffset.Now, "Endpoint", message));
        while (_audit.Count > 100) _audit.RemoveAt(_audit.Count - 1);
    });
    private void SetState(SessionState state) => Ui(() =>
    {
        if (state == SessionState.Disconnected) ResetState();
        else
        {
            if (_machine.State == state) return;
            _machine.MoveTo(state);
            StateText.Text = state == SessionState.Connected ? "Secure channel ready" : state.ToString();
            TopStatus.Text = "●  " + StateText.Text;
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
        _renderTimer.Stop(); Interlocked.Exchange(ref _latestFrame, null);
        RemoteImage.Source = null;
        ViewerPanel.Visibility = SharingPanel.Visibility = ApprovalPanel.Visibility = PairingPanel.Visibility = Visibility.Collapsed;
        ConnectionCards.Visibility = Visibility.Visible;
        StateText.Text = "Disconnected"; PeerText.Text = "No active peer";
        CipherText.Text = "Mutual TLS · verified device key";
        TopStatus.Text = _agent is null ? "●  Availability off" : "●  Available on this LAN";
        ConnectButton.IsEnabled = _identity is not null && !_closing;
        DisconnectButton.Visibility = Visibility.Collapsed;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            _identity = await Task.Run(DeviceIdentity.LoadOrCreateWindows);
            if (_closing) { _identity.Dispose(); return; }
            IdentityBox.Text = NumericDeviceId.Format(NumericDeviceId.FromFingerprint(_identity.DeviceId));
            FullFingerprintBox.Text = _identity.DeviceId;
            MachineNameText.Text = Environment.MachineName;
            LocalAddressText.Text = "Share your ID. No IP address needed on this LAN.";
            FooterText.Text = "RemoteDesk  /  LAN screen viewing · v0.2";
            try
            {
                if (File.Exists(_settingsPath) && new FileInfo(_settingsPath).Length <= 131072)
                    _devices = (JsonSerializer.Deserialize<List<SavedDevice>>(await File.ReadAllTextAsync(_settingsPath)) ?? [])
                        .Where(d => d.Name is { Length: > 0 and <= 64 } && d.Code is { Length: 12 } && d.Code.All(char.IsAsciiDigit)).Take(100).ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log("Saved devices could not be loaded."); }
            FilterDevices(); ConnectButton.IsEnabled = true;
            Log("Persistent numeric ID ready. Every screen-sharing session needs Host approval.");
            ListenCheck.IsChecked = true; // Available while the visible app is running; never unattended.
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = "Device identity could not be initialized. Check your Windows user profile and key-store access.";
            Log("Identity initialization failed; no connection allowed.");
        }
    }
    private async void Listen_Changed(object sender, RoutedEventArgs e)
    {
        if (_changingListener || _closing || _identity is null) return;
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
                try
                {
                    _discovery = new LanDiscovery(_identity, agent.Port, DisplayName()); _discovery.Start();
                    ListenerBadge.Text = "● AVAILABLE"; TopStatus.Text = "●  Available on this LAN";
                    Log("Attended listener and signed numeric-ID discovery started.");
                }
                catch (SocketException)
                {
                    ListenerBadge.Text = "MANUAL ONLY";
                    ErrorText.Text = "Numeric-ID discovery could not bind UDP 45832. Close other instances or use Advanced manual setup.";
                }
            }
            else
            {
                if (_discovery is not null) { var discovery = _discovery; _discovery = null; await discovery.DisposeAsync(); }
                if (_agent is not null) { var agent = _agent; _agent = null; await agent.DisposeAsync(); }
                ListenerBadge.Text = "OFFLINE"; TopStatus.Text = "●  Availability off";
                Log("Incoming availability and discovery stopped.");
            }
        }
        catch (SocketException)
        {
            _changingListener = true; ListenCheck.IsChecked = false; _changingListener = false;
            ErrorText.Text = "Cannot listen on TCP 45831. Close other RemoteDesk instances or check Windows Firewall.";
        }
        finally { ListenCheck.IsEnabled = true; }
    }
    private static string DisplayName() => new(Environment.MachineName.Where(c => !char.IsControl(c)).Take(64).ToArray());
    private async Task<bool> AskApprovalAsync(ApprovalRequest request, CancellationToken token)
    {
        _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ApprovalDetails.Text = $"Device: {NumericDeviceId.Format(NumericDeviceId.FromFingerprint(request.DeviceId))}\nName (unverified): {request.DisplayName}\nVerification code: {request.VerificationCode}\nRequested: {(request.ViewScreen ? "VIEW YOUR PRIMARY DISPLAY" : "protocol only")}\nExpires in 45 seconds.";
        ApproveButton.Content = request.ViewScreen ? "Approve & share my screen" : "Approve protocol only";
        ApprovalPanel.Visibility = Visibility.Visible; Activate();
        _ = Dispatcher.BeginInvoke(new Action(() => ApprovalPanel.BringIntoView()));
        Log("Incoming request requires explicit local approval.");
        try { return await _approval.Task.WaitAsync(token); }
        finally { _approval = null; ApprovalPanel.Visibility = Visibility.Collapsed; }
    }
    private void Approve_Click(object sender, RoutedEventArgs e) => _approval?.TrySetResult(true);
    private void Reject_Click(object sender, RoutedEventArgs e) => _approval?.TrySetResult(false);
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_identity is null || _busy) return;
        ErrorText.Text = ""; _busy = true; _incoming = false;
        _operation?.Dispose(); _operation = new();
        ConnectButton.IsEnabled = false; DisconnectButton.Visibility = Visibility.Visible;
        try
        {
            string address, pin; int port;
            if (ManualCheck.IsChecked == true)
            {
                address = AddressBox.Text.Trim();
                if (Uri.CheckHostName(address) == UriHostNameType.Unknown) throw new ArgumentException("Enter a valid LAN IP address or hostname.");
                if (!int.TryParse(PortBox.Text, out port) || port is < 1 or > 65535) throw new ArgumentException("Invalid Host port.");
                pin = DeviceIdentity.NormalizeId(FingerprintBox.Text);
            }
            else
            {
                string code = NumericDeviceId.Normalize(TargetIdBox.Text);
                if (code == NumericDeviceId.FromFingerprint(_identity.DeviceId)) throw new ArgumentException("Enter another device's ID, not your own.");
                StateText.Text = "Finding device on LAN…";
                var target = await LanDiscovery.ResolveAsync(code, _operation.Token);
                var known = _devices.FirstOrDefault(d => d.Code == code && d.Fingerprint is not null);
                if (known is not null && known.Fingerprint != target.Fingerprint) throw new AuthenticationException("Saved identity changed.");
                address = target.Address.ToString(); port = target.Port; pin = target.Fingerprint;
            }
            if (pin == _identity.DeviceId) throw new ArgumentException("Choose a different device.");
            var session = await SecureSession.ConnectAsync(address, port, pin, DisplayName(), _identity, SetState, _operation.Token,
                requestScreen: true, verificationCode: code => Ui(() =>
                {
                    PairingText.Text = $"Verification code: {code} — compare this with the Host before they approve.";
                    PairingPanel.Visibility = Visibility.Visible;
                }));
            if (_closing || _operation.IsCancellationRequested) { await session.DisposeAsync(); ResetState(); return; }
            AttachSession(session, true); session.Start();
            try { await SaveDeviceAsync(NumericDeviceId.FromFingerprint(pin), pin); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Connected, but the verified target could not be saved."); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or SocketException or AuthenticationException or UnauthorizedAccessException or OperationCanceledException)
        {
            ErrorText.Text = ex switch
            {
                ArgumentException or InvalidDataException => ex.Message,
                AuthenticationException => "Device authentication failed or a saved key changed. Verify the full fingerprint in Advanced setup; never bypass a changed key.",
                UnauthorizedAccessException => "The Host declined screen sharing.",
                OperationCanceledException => "Connection cancelled or approval timed out.",
                IOException => ex.Message.StartsWith("Device not found", StringComparison.Ordinal) ? ex.Message : "Connection failed. Update both endpoints to v0.2 and check Private-network firewall rules.",
                _ => "Cannot reach the Host. Check that RemoteDesk is open and allowed on its Private network."
            };
            Log("Connection did not complete."); ResetState();
        }
    }
    private void AttachSession(SecureSession session, bool owned)
    {
        _session = session; CipherText.Text = session.Cipher;
        PairingText.Text = $"Verified session code: {session.VerificationCode}";
        PeerText.Text = $"Peer: {NumericDeviceId.Format(NumericDeviceId.FromFingerprint(session.PeerId))} · {session.PeerName}";
        if (session.CanReceiveScreen)
        {
            ViewerPanel.Visibility = Visibility.Visible; ConnectionCards.Visibility = Visibility.Collapsed;
            ViewerStatus.Text = "Secure channel established — waiting for the first screen frame…";
            StateText.Text = "Waiting for screen"; _receivedFrames = 0; _lastFrame = DateTimeOffset.UtcNow;
            session.ScreenReceived += jpeg => { if (ReferenceEquals(_session, session)) Interlocked.Exchange(ref _latestFrame, jpeg); };
            session.ScreenUnavailable += () => Ui(() =>
            {
                if (!ReferenceEquals(_session, session)) return;
                _receivedFrames = 0; Interlocked.Exchange(ref _latestFrame, null);
                RemoteImage.Source = null; ViewerStatus.Text = "Host cannot capture this desktop. Ask them to unlock their normal Windows desktop and reconnect.";
                StateText.Text = "Screen unavailable";
            });
            _renderTimer.Start();
        }
        if (session.CanSendScreen)
        {
            SharingPanel.Visibility = Visibility.Visible;
            StateText.Text = "Starting screen share";
            session.StartScreenProducer(token => ShareScreenAsync(session, token));
        }
        Log("Authenticated session approved; screen permission enforced independently of transport state.");
        _ = ObserveSessionAsync(session, owned);
    }
    private async Task ShareScreenAsync(SecureSession session, CancellationToken token)
    {
        try
        {
            using var capture = new DesktopCapture(); bool first = true;
            while (!token.IsCancellationRequested)
            {
                byte[] jpeg = capture.Capture();
                await session.SendScreenAsync(jpeg, token).ConfigureAwait(false);
                if (first)
                {
                    first = false;
                    Ui(() => { if (ReferenceEquals(_session, session)) { StateText.Text = "Sharing primary display"; TopStatus.Text = "●  Your screen is being shared"; } });
                }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Ui(() =>
            {
                if (!ReferenceEquals(_session, session)) return;
                StateText.Text = "Screen unavailable";
                ErrorText.Text = "Screen capture stopped. Unlock your normal Windows desktop and reconnect. Protected/UAC desktops are not supported.";
                Log("Screen producer stopped; no further screen data is sent.");
            });
            if (!token.IsCancellationRequested && !session.Completion.IsCompleted)
            {
                try { await session.NotifyScreenUnavailableAsync(token).ConfigureAwait(false); }
                catch (Exception sendError) when (sendError is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException) { }
            }
        }
    }
    private async void RenderFrame(object? sender, EventArgs e)
    {
        var session = _session;
        if (session is null) return;
        byte[]? jpeg = Interlocked.Exchange(ref _latestFrame, null);
        if (jpeg is null)
        {
            if (_receivedFrames > 0 && DateTimeOffset.UtcNow - _lastFrame > TimeSpan.FromSeconds(5))
                ViewerStatus.Text = "No recent screen frame — the last image may be stale.";
            return;
        }
        try
        {
            using var input = new MemoryStream(jpeg, false);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = input; image.EndInit(); image.Freeze();
            RemoteImage.Source = image; _lastFrame = DateTimeOffset.UtcNow; _receivedFrames++;
            ViewerStatus.Text = $"Live primary display · {image.PixelWidth} × {image.PixelHeight} · view only · frames {_receivedFrames}";
            StateText.Text = "Viewing remote desktop"; TopStatus.Text = "●  Live encrypted screen";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            ErrorText.Text = "An invalid screen image was received. The session has been closed.";
            await session.DisconnectAsync();
        }
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
        if (_incoming && _session is null) _agent?.CancelCurrent();
        if (_session is not null) { SetState(SessionState.Disconnecting); await _session.DisconnectAsync(); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (_identity is not null) { Clipboard.SetText(NumericDeviceId.FromFingerprint(_identity.DeviceId)); Log("Numeric device ID copied."); } }
        catch (System.Runtime.InteropServices.COMException) { ErrorText.Text = "Clipboard is busy. Select and copy your ID manually."; }
    }
    private void FilterDevices()
    {
        if (DeviceList is null) return;
        string search = SearchBox.Text.Trim();
        var filtered = _devices.Where(d => d.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || d.Code.Contains(search.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)).ToList();
        DeviceList.ItemsSource = filtered; EmptyDevices.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterDevices();
    private async Task SaveDeviceAsync(string code, string? pin)
    {
        var old = _devices.FirstOrDefault(d => d.Code == code);
        _devices.RemoveAll(d => d.Code == code);
        _devices.Insert(0, new("Device " + NumericDeviceId.Format(code), code, pin ?? old?.Fingerprint));
        if (_devices.Count > 100) _devices.RemoveAt(_devices.Count - 1);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(_devices)); FilterDevices();
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveDeviceAsync(NumericDeviceId.Normalize(TargetIdBox.Text), null); Log("Numeric target saved. Every connection still needs local approval."); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { ErrorText.Text = ex is ArgumentException ? ex.Message : "Could not save target."; }
    }
    private void Device_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is SavedDevice device) { TargetIdBox.Text = NumericDeviceId.Format(device.Code); ManualCheck.IsChecked = false; }
    }
    private void Home_Click(object sender, RoutedEventArgs e) => TargetIdBox.Focus();
    private void Guide_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Run v0.2 on both devices on the SAME PRIVATE LAN. Share the Host's 12-digit ID. Enter that ID and select View remote screen. Compare the six-digit verification code on both devices, then the Host selects Approve & share my screen.\n\nAllow TCP 45831 and UDP 45832 on Private networks if Windows Firewall blocks the app. See run/README.md and scripts/Enable-LanFirewall.ps1. Do not open these ports to the internet.\n\nThis version streams the primary display (JPEG, up to about 4 FPS). It is VIEW ONLY: no mouse, keyboard, files, clipboard, unattended access or UAC/Secure Desktop. Internet ID routing and relay require a deployed server and are not configured.",
        "RemoteDesk · ID connection & screen sharing", MessageBoxButton.OK, MessageBoxImage.Information);
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true; if (_closing) return; _closing = true;
        _renderTimer.Stop(); _operation?.Cancel(); _approval?.TrySetResult(false);
        if (_session is not null) await _session.DisconnectAsync();
        if (_discovery is not null) await _discovery.DisposeAsync();
        if (_agent is not null) await _agent.DisposeAsync();
        _identity?.Dispose(); _operation?.Dispose();
        _canClose = true; _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
