using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RemoteDesk.Files;
using RemoteDesk.Protocol;
using RemoteDesk.Transport;
namespace RemoteDesk.Desktop;

public partial class SessionView : UserControl, IAsyncDisposable
{
    public SecureSession Session { get; }
    public Action<bool>? FullScreen { get; set; }
    public Func<Task>? CloseRequested { get; set; }
    public Func<string, Task>? ShareLocalFolder { get; set; }
    public Action? StopLocalSharing { get; set; }
    public Func<string>? ConfiguredFolder { get; set; }
    public Action<string>? ActionReported { get; set; }
    private bool _appliedAnimations, _restoringAnimation;
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _decodeTask;
    private readonly CancellationToken _lifetime;
    private readonly System.Windows.Threading.DispatcherTimer _stale = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _ready, _fullScreen, _moving;
    private volatile bool _visible;
    private int _imageWidth, _imageHeight, _disposed;
    private long _lastMove, _lastProgress;
    private long _frameCount, _measuredFrames;
    private readonly Stopwatch _meter = Stopwatch.StartNew();
    private DateTimeOffset _lastFrame = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _transfer;
    private string _directory = "";
    public SessionView(SecureSession session, int defaultFps, int defaultWidth = 1440)
    {
        Session = session; _lifetime = _stop.Token; InitializeComponent();
        int[] rates = [5, 15, 20, 24, 30]; FpsBox.SelectedIndex = Math.Max(0, Array.IndexOf(rates, defaultFps));
        QualityBox.SelectedIndex = defaultWidth <= 960 ? 0 : defaultWidth >= 1920 ? 2 : 1;
        FilesButton.IsEnabled = ShareFolderButton.IsEnabled = session.Permissions.FileAccess;
        FilesButton.ToolTip = ShareFolderButton.ToolTip = session.Permissions.FileAccess ? "Browse the other device’s explicitly shared folder, or choose which of your folders to share back." : "Files was not granted. Reconnect with Files requested and check file exchange on the Host approval.";
        PermissionsText.Text = $"Granted · Screen: {session.Permissions.ViewScreen} · Control: {session.Permissions.ControlInput} · Files: {session.Permissions.FileAccess} · Animations: {session.Permissions.DesktopEffects}";
        AdminButton.IsEnabled = LockButton.IsEnabled = ControlCheck.IsEnabled = !session.IsHost && session.Permissions.ControlInput;
        AnimationsCheck.IsEnabled = !session.IsHost && session.Permissions.DesktopEffects;
        AnimationsCheck.ToolTip = session.IsHost ? "This option is controlled by the Controller." : session.Permissions.DesktopEffects ? "Temporarily disable Host desktop animations; restores on disconnect." : "Animations was not granted. Select Animations before connecting; the Host must enable its policy and check the animation grant.";
        AdminButton.ToolTip = LockButton.ToolTip = session.IsHost ? "Requests are sent from the Controller." : session.Permissions.ControlInput ? "Requires enabled Host policy and fresh local confirmation. Any refusal is shown below and in Recent activity." : "Control was not granted. Reconnect and grant Control on the Host.";
        ControlCheck.IsChecked = !session.IsHost && session.Permissions.ControlInput;
        FpsBox.IsEnabled = QualityBox.IsEnabled = DisplayBox.IsEnabled = session.CanReceiveScreen;
        if (session.IsHost)
        {
            WaitingTitle.Text = "Your screen is shared with this Controller";
            WaitingDetail.Text = $"Granted: screen {session.Permissions.ViewScreen}, input {session.Permissions.ControlInput}, files {session.Permissions.FileAccess}. Disconnect to revoke every permission. Physical input blocking, if requested, needs fresh local confirmation and expires after 30 seconds.";
        }
        session.ScreenReceived += QueueFrame;
        session.ScreenUnavailable += ScreenUnavailable;
        _decodeTask = DecodeAsync();
        _stale.Tick += (_, _) =>
        {
            if (_frameCount > 0 && DateTimeOffset.UtcNow - _lastFrame > TimeSpan.FromSeconds(5)) ViewerStatus.Text = "Screen paused — last image may be stale.";
        };
        _stale.Start(); _ready = true;
    }
    public void SetStatus(string status) => _ = Dispatcher.BeginInvoke(new Action(() => { ActionStatus.Text = status; ActionReported?.Invoke(status); }));
    public void SetLocalFolder(string? folder)
    {
        LocalFolderText.Text = folder is null ? "Your folder is NOT shared with this device. Choose Share my folder to expose a selected folder only." : "You are sharing: " + folder;
        StopSharingButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
    }
    public async Task ShareFolderAsync(string folder)
    {
        if (!Session.Permissions.FileAccess || ShareLocalFolder is null) throw new UnauthorizedAccessException("File exchange was not granted.");
        await ShareLocalFolder(folder); SetLocalFolder(folder); SetStatus("Folder shared with this device. On the other device, open Remote files and Refresh.");
    }
    private async void ShareFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Share this folder with the connected device (read and new uploads)" };
        string? configured = ConfiguredFolder?.Invoke(); if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) dialog.InitialDirectory = configured;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await ShareFolderAsync(dialog.FolderName); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { SetStatus(ex.Message); }
    }
    private void StopSharing_Click(object sender, RoutedEventArgs e) { StopLocalSharing?.Invoke(); SetLocalFolder(null); SetStatus("Local folder sharing stopped. Already accepted disk operations may finish; future requests are refused."); }
    private void QueueFrame(byte[] jpeg) => _frames.Writer.TryWrite(jpeg);
    private void ScreenUnavailable() => _ = Dispatcher.BeginInvoke(new Action(() =>
    {
        RemoteImage.Source = null; WaitingPanel.Visibility = Visibility.Visible; WaitingTitle.Text = "Host screen unavailable";
        WaitingDetail.Text = "Ask the Host to unlock their normal Windows desktop and reconnect. UAC/protected desktops cannot be captured.";
        ViewerStatus.Text = "Screen unavailable"; _frameCount = 0;
    }));
    private async Task DecodeAsync()
    {
        try
        {
            await foreach (var jpeg in _frames.Reader.ReadAllAsync(_lifetime).ConfigureAwait(false))
            {
                if (!_visible) continue;
                using var input = new MemoryStream(jpeg, false);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
                await Dispatcher.InvokeAsync(() =>
                {
                    RemoteImage.Source = bitmap; WaitingPanel.Visibility = Visibility.Collapsed;
                    _imageWidth = bitmap.PixelWidth; _imageHeight = bitmap.PixelHeight; _frameCount++; _measuredFrames++;
                    _lastFrame = DateTimeOffset.UtcNow; ApplyDisplay();
                    if (_meter.ElapsedMilliseconds >= 1000 || _frameCount == 1)
                    {
                        double fps = _measuredFrames / Math.Max(.001, _meter.Elapsed.TotalSeconds);
                        ViewerStatus.Text = $"Live · {bitmap.PixelWidth} × {bitmap.PixelHeight} · {fps:F1} received FPS · frames {_frameCount} · {(Session.Permissions.ControlInput ? "input permitted" : "view only")} · Ctrl+Alt+F12 releases control";
                        _measuredFrames = 0; _meter.Restart();
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        { SetStatus("Screen decoding failed; session closed."); await Session.DisconnectAsync(); }
    }
    private async Task<bool> OptionsAsync()
    {
        if (!_ready || !Session.CanReceiveScreen || _stop.IsCancellationRequested) return false;
        bool entered = false;
        try
        {
            await _profileGate.WaitAsync(_lifetime); entered = true;
            int fps = int.Parse(((ComboBoxItem)FpsBox.SelectedItem).Content.ToString()!);
            int width = QualityBox.SelectedIndex switch { 0 => 960, 2 => 1920, _ => 1440 };
            int quality = QualityBox.SelectedIndex switch { 0 => 40, 2 => 80, _ => 55 };
            bool animations = AnimationsCheck.IsChecked == true;
            await Session.RequestAsync(RemoteCommand.Create(Operation.SetStream) with { Fps = _visible ? fps : 2, Width = width, Quality = quality, DisableAnimations = animations }, _lifetime);
            _appliedAnimations = animations; return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            _restoringAnimation = true; AnimationsCheck.IsChecked = _appliedAnimations; _restoringAnimation = false;
            if (!_stop.IsCancellationRequested) SetStatus(ex.Message); return false;
        }
        finally { if (entered) _profileGate.Release(); }
    }
    private async void Profile_Changed(object sender, SelectionChangedEventArgs e) => await OptionsAsync();
    private async void Animations_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _restoringAnimation) return;
        if (await OptionsAsync()) SetStatus(_appliedAnimations ? "Animations disabled on the Host for this session." : "Host animation preferences restored.");
    }
    private void Display_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) ApplyDisplay(); }
    private void ApplyDisplay()
    {
        bool actual = DisplayBox.SelectedIndex == 1;
        RemoteImage.Width = actual && _imageWidth > 0 ? _imageWidth : double.NaN;
        RemoteImage.Height = actual && _imageHeight > 0 ? _imageHeight : double.NaN;
        InputSurface.Width = actual && _imageWidth > 0 ? _imageWidth : double.NaN;
        InputSurface.Height = actual && _imageHeight > 0 ? _imageHeight : double.NaN;
        ImageScroller.HorizontalScrollBarVisibility = ImageScroller.VerticalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        RemoteImage.Stretch = DisplayBox.SelectedIndex == 2 ? Stretch.Fill : Stretch.Uniform;
    }
    private async void View_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _visible = IsVisible;
        if (!_ready) return;
        if (!_visible) await ReleaseAsync();
        await OptionsAsync();
    }
    private bool InputEnabled => _ready && ControlCheck.IsChecked == true && Session.Permissions.ControlInput && !Session.IsHost && _imageWidth > 0;
    private bool Point(MouseEventArgs e, out int x, out int y)
    {
        x = y = 0; if (!InputEnabled) return false;
        var p = e.GetPosition(RemoteImage); double width = RemoteImage.ActualWidth, height = RemoteImage.ActualHeight;
        if (width <= 0 || height <= 0) return false;
        double left = 0, top = 0;
        if (RemoteImage.Stretch == Stretch.Uniform)
        {
            double scale = Math.Min(width / _imageWidth, height / _imageHeight);
            double w = _imageWidth * scale, h = _imageHeight * scale;
            left = (width - w) / 2; top = (height - h) / 2; width = w; height = h;
        }
        if (p.X < left || p.Y < top || p.X > left + width || p.Y > top + height) return false;
        x = (int)Math.Clamp((p.X - left) / width * 65535, 0, 65535); y = (int)Math.Clamp((p.Y - top) / height * 65535, 0, 65535); return true;
    }
    private async Task InputAsync(InputEvent input)
    {
        try { await Session.SendInputAsync(input, _lifetime); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException) { if (!_stop.IsCancellationRequested) SetStatus("Input not sent; check session/permissions."); }
    }
    private async void Surface_Move(object sender, MouseEventArgs e)
    {
        if (_moving || Environment.TickCount64 - _lastMove < 16 || !Point(e, out int x, out int y)) return;
        _lastMove = Environment.TickCount64; _moving = true;
        try { await InputAsync(new(InputKind.Move, x, y)); } finally { _moving = false; }
    }
    private static int ButtonCode(MouseButton button) => button switch { MouseButton.Left => 1, MouseButton.Right => 2, MouseButton.Middle => 3, MouseButton.XButton1 => 4, _ => 5 };
    private async void Surface_Down(object sender, MouseButtonEventArgs e)
    {
        if (!Point(e, out int x, out int y)) return; InputSurface.Focus(); Mouse.Capture(InputSurface); e.Handled = true;
        await InputAsync(new(InputKind.Button, x, y, ButtonCode(e.ChangedButton)));
    }
    private async void Surface_Up(object sender, MouseButtonEventArgs e)
    {
        if (Point(e, out int x, out int y)) { e.Handled = true; await InputAsync(new(InputKind.Button, x, y, ButtonCode(e.ChangedButton), true)); }
        else if (InputEnabled) await ReleaseAsync();
        Mouse.Capture(null);
    }
    private async void Surface_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!Point(e, out int x, out int y)) return; e.Handled = true; await InputAsync(new(InputKind.Wheel, x, y, Delta: Math.Clamp(e.Delta, -1200, 1200)));
    }
    private async void Surface_KeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11) { e.Handled = true; ToggleFullScreen(); return; }
        if (key == Key.Escape && _fullScreen) { e.Handled = true; _fullScreen = false; FullScreen?.Invoke(false); return; }
        if (key == Key.F12 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        { e.Handled = true; ControlCheck.IsChecked = false; await ReleaseAsync(); return; }
        if (!InputEnabled) return; int code = KeyInterop.VirtualKeyFromKey(key);
        if (code is > 0 and <= 254) { e.Handled = true; await InputAsync(new(InputKind.Key, Code: code)); }
    }
    private async void Surface_KeyUp(object sender, KeyEventArgs e)
    {
        if (!InputEnabled) return; int code = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (code is > 0 and <= 254) { e.Handled = true; await InputAsync(new(InputKind.Key, Code: code, IsUp: true)); }
    }
    private async Task ReleaseAsync() { if (Session.Permissions.ControlInput && !Session.IsHost) await InputAsync(new(InputKind.ReleaseAll)); }
    private async void Surface_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => await ReleaseAsync();
    private async void Control_Changed(object sender, RoutedEventArgs e) { if (_ready && ControlCheck.IsChecked != true) await ReleaseAsync(); }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void ToggleFullScreen() { _fullScreen = Window.GetWindow(this)?.WindowStyle != WindowStyle.None; FullScreen?.Invoke(_fullScreen); InputSurface.Focus(); }
    private async void Disconnect_Click(object sender, RoutedEventArgs e) { if (CloseRequested is not null) await CloseRequested(); }
    private async void Admin_Click(object sender, RoutedEventArgs e) => await SpecialAsync(Operation.RequestAdmin);
    private async void Lock_Click(object sender, RoutedEventArgs e) => await SpecialAsync(Operation.RequestInputLock);
    private async Task SpecialAsync(Operation op)
    {
        SetStatus(op == Operation.RequestAdmin ? "Admin request sent — waiting for the Host’s local confirmation. The normal UAC prompt appears on the HOST, not here." : "Input-block request sent — waiting for fresh Host consent.");
        try { var reply = await Session.RequestAsync(RemoteCommand.Create(op), _lifetime); SetStatus(reply.Path == "already-elevated" ? "The Host is already running as administrator. No restart or extra UAC prompt is needed." : op == Operation.RequestAdmin ? "Host accepted UAC restart; reconnect after their approval." : "Host accepted physical input blocking (up to 30 seconds; Ctrl+Alt+Del releases locally)."); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { SetStatus(ex.Message); }
    }
    private async void Files_Click(object sender, RoutedEventArgs e)
    {
        FilesColumn.Width = FilesColumn.Width.Value > 0 ? new GridLength(0) : new GridLength(360);
        if (FilesColumn.Width.Value > 0) await ListAsync();
    }
    private async Task ListAsync()
    {
        try
        {
            RemoteFolderInfo.Text = "Checking the other device’s selected folder…";
            var info = await Session.RequestAsync(RemoteCommand.Create(Operation.ShareInfo), _lifetime);
            var reply = await Session.RequestAsync(RemoteCommand.Create(Operation.ListFiles, _directory), _lifetime);
            FileList.ItemsSource = reply.Entries ?? []; FolderPath.Text = "/" + _directory;
            RemoteFolderInfo.Text = "Other device shares: " + info.Path;
            TransferText.Text = $"{reply.Entries?.Length ?? 0} entries. Double-click a folder. Upload writes to that device’s shared root; Download saves locally.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { FileList.ItemsSource = null; RemoteFolderInfo.Text = "No accessible remote folder"; TransferText.Text = ex.Message; SetStatus(ex.Message); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ListAsync();
    private async void Up_Click(object sender, RoutedEventArgs e) { _directory = _directory.Contains('/') ? _directory[.._directory.LastIndexOf('/')] : ""; await ListAsync(); }
    private async void File_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry { Directory: true } entry) { _directory = string.IsNullOrEmpty(_directory) ? entry.Name : _directory + "/" + entry.Name; await ListAsync(); }
    }
    private sealed class ProgressSink(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private IProgress<string> TransferProgress() => new ProgressSink(text =>
    {
        long now = Environment.TickCount64; if (now - Interlocked.Read(ref _lastProgress) < 150 && !text.Contains("complete", StringComparison.Ordinal)) return;
        Interlocked.Exchange(ref _lastProgress, now); _ = Dispatcher.BeginInvoke(new Action(() => TransferText.Text = text));
    });
    private async Task TransferAsync(Func<CancellationToken, Task> action)
    {
        if (_transfer is not null) { TransferText.Text = "Pause the current transfer first."; return; }
        _transfer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        try { await action(_transfer.Token); await ListAsync(); }
        catch (OperationCanceledException) { TransferText.Text = "Paused/disconnected. Reconnect and choose the same file/destination to resume."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { TransferText.Text = ex.Message; }
        finally { _transfer.Dispose(); _transfer = null; }
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Upload to the other device’s shared-folder root (no overwrite)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await TransferAsync(token => FileTransferClient.UploadAsync(Session, dialog.FileName, TransferProgress(), token));
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry { Directory: false } entry) return;
        var dialog = new SaveFileDialog { FileName = entry.Name, Title = "Download / resume to the same destination", OverwritePrompt = false };
        string remote = string.IsNullOrEmpty(_directory) ? entry.Name : _directory + "/" + entry.Name;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await TransferAsync(token => FileTransferClient.DownloadAsync(Session, remote, dialog.FileName, TransferProgress(), token));
    }
    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => _transfer?.Cancel();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await ReleaseAsync(); _stop.Cancel(); _transfer?.Cancel(); _stale.Stop();
        Session.ScreenReceived -= QueueFrame; Session.ScreenUnavailable -= ScreenUnavailable; _frames.Writer.TryComplete();
        await _decodeTask; RemoteImage.Source = null; _stop.Dispose();
    }
}
