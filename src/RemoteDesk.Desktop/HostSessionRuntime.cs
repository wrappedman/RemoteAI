using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RemoteDesk.Capture;
using RemoteDesk.Files;
using RemoteDesk.Protocol;
using RemoteDesk.Transport;
using RemoteDesk.Windows;
namespace RemoteDesk.Desktop;

public sealed record StreamProfile(int Fps = 20, int Width = 1440, int Quality = 55);
public sealed class HostSessionRuntime : IDisposable
{
    private readonly SecureSession _session;
    private AppSettings _settings;
    private readonly InputController? _input;
    private readonly DesktopEffects _effects = new();
    private readonly object _effectsGate = new();
    private SharedFolderService? _files;
    private bool _sessionFolder;
    public string? SharedFolder { get; private set; }
    private readonly Func<Operation, Task<bool>> _confirm;
    private readonly Action<string> _status;
    private readonly Func<bool> _isElevated;
    private StreamProfile _profile;
    private bool _closed;
    public HostSessionRuntime(SecureSession session, AppSettings settings, Func<Operation, Task<bool>> confirm, Action<string> status, Func<bool>? isElevated = null)
    {
        _session = session; _settings = settings; _confirm = confirm; _status = status; _isElevated = isElevated ?? ElevationLauncher.IsElevated;
        _profile = new(settings.DefaultFps, settings.DefaultWidth);
        if (session.IsHost && session.Permissions.ControlInput)
        {
            _input = new(); _input.Status += status; session.InputReceived += _input.Post;
        }
        // The Host's local approval authorizes its configured root. A Controller must
        // explicitly select Share my folder before exposing any local data back to the Host.
        if (session.IsHost) RefreshConfiguredFolder();
        session.CommandHandler = HandleAsync;
        if (session.CanSendScreen) session.StartScreenProducer(ProduceAsync);
    }
    public void UpdateSettings(AppSettings settings)
    {
        Volatile.Write(ref _settings, settings);
        if (!settings.AllowEffects) lock (_effectsGate) _effects.SetDisabled(false);
        if (!settings.AllowFiles) StopSharing();
        else if (_session.IsHost && !_sessionFolder) RefreshConfiguredFolder();
    }
    private void RefreshConfiguredFolder()
    {
        if (!_session.Permissions.FileAccess || !_settings.AllowFiles) return;
        if (string.IsNullOrEmpty(_settings.SharedFolder)) { Volatile.Write(ref _files, null); SharedFolder = null; return; }
        try { ShareFolder(_settings.SharedFolder, false); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { Volatile.Write(ref _files, null); SharedFolder = null; _status("Local shared folder unavailable. Choose Share my folder to select an accessible folder."); }
    }
    public void ShareFolder(string folder, bool sessionOnly = true)
    {
        if (_closed || !_session.Permissions.FileAccess || !_settings.AllowFiles)
            throw new UnauthorizedAccessException("File exchange was not granted or local file policy is disabled. Enable Files and reconnect with that permission.");
        // Use the same root restrictions for Settings and session-specific sharing.
        new AppSettings { SharedFolder = folder }.Validate();
        var files = new SharedFolderService(folder, AppSettings.TransferCache, _session.PeerId);
        Volatile.Write(ref _files, files); SharedFolder = Path.GetFullPath(folder); _sessionFolder = sessionOnly;
    }
    public void StopSharing() { Volatile.Write(ref _files, null); SharedFolder = null; _sessionFolder = true; }
    private async Task<RemoteCommand> HandleAsync(RemoteCommand command, CancellationToken token)
    {
        if (_closed) return command.Fail("Session ended.");
        switch (command.Operation)
        {
            case Operation.SetStream:
                if (command.DisableAnimations && (!_session.Permissions.DesktopEffects || !_settings.AllowEffects)) return command.Fail("Animations are not permitted. Enable animations in Host Settings and grant Animations during approval; reconnect if not granted.");
                lock (_effectsGate)
                {
                    if (_closed) return command.Fail("Session ended.");
                    try { _effects.SetDisabled(command.DisableAnimations); }
                    catch (Win32Exception) { return command.Fail("Windows refused the temporary animation change."); }
                    _profile = new(command.Fps, command.Width, command.Quality);
                }
                return command.Reply();
            case Operation.RequestInputLock:
                if (!_settings.AllowInputLock || _input is null) return command.Fail("Host Settings: enable physical-input-block requests and grant Control, then retry. Fresh local consent is always required.");
                if (!await _confirm(command.Operation)) return command.Fail("Host declined physical-input blocking.");
                bool blocked;
                try { blocked = await _input.BlockPhysicalInputFor30SecondsAsync(); }
                catch (TimeoutException) { return command.Fail("Input worker did not respond; no block confirmation available."); }
                return blocked ? command.Reply() : command.Fail("Windows refused physical-input blocking (elevation may be required), or a block is already active.");
            case Operation.RequestAdmin:
                if (!_settings.AllowAdminRequests) return command.Fail("Host Settings: enable admin restart requests, then retry. A local confirmation and the normal Windows UAC prompt are required.");
                if (_isElevated()) return command.Reply() with { Path = "already-elevated" };
                return await _confirm(command.Operation) ? command.Reply() : command.Fail("Host declined the UAC restart request.");
            default:
                var files = Volatile.Read(ref _files);
                return files is null ? command.Fail("No folder is shared by the other device. On that device, choose Share my folder in this session, then Refresh here.") : await files.HandleAsync(command, token);
        }
    }
    private async Task ProduceAsync(CancellationToken token)
    {
        try
        {
            using var capture = new DesktopCapture(); bool first = true;
            while (!token.IsCancellationRequested)
            {
                long start = Stopwatch.GetTimestamp(); var profile = Volatile.Read(ref _profile);
                byte[] jpeg = capture.Capture(profile.Width, profile.Quality);
                await _session.SendScreenAsync(jpeg, token).ConfigureAwait(false);
                if (first) { first = false; _status("Sharing primary display. Host can stop this session at any time."); }
                double left = 1000d / profile.Fps - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (left > 1) await Task.Delay(TimeSpan.FromMilliseconds(left), token).ConfigureAwait(false);
                else await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            _status("Screen unavailable: unlock the normal desktop and reconnect. Protected/UAC desktops are not supported.");
            if (!token.IsCancellationRequested)
            {
                try { await _session.NotifyScreenUnavailableAsync(token).ConfigureAwait(false); }
                catch (Exception send) when (send is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }
    public void Dispose()
    {
        lock (_effectsGate) { _closed = true; _effects.Dispose(); }
        if (_input is not null) { _session.InputReceived -= _input.Post; _input.Dispose(); }
    }
}
