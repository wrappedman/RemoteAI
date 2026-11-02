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
    private readonly AppSettings _settings;
    private readonly InputController? _input;
    private readonly DesktopEffects _effects = new();
    private readonly object _effectsGate = new();
    private readonly SharedFolderService? _files;
    private readonly Func<Operation, Task<bool>> _confirm;
    private readonly Action<string> _status;
    private StreamProfile _profile;
    private bool _closed;
    public HostSessionRuntime(SecureSession session, AppSettings settings, Func<Operation, Task<bool>> confirm, Action<string> status)
    {
        _session = session; _settings = settings; _confirm = confirm; _status = status;
        _profile = new(settings.DefaultFps, settings.DefaultWidth);
        if (session.Permissions.ControlInput)
        {
            _input = new(); _input.Status += status; session.InputReceived += _input.Post;
        }
        if (session.Permissions.FileAccess && !string.IsNullOrEmpty(settings.SharedFolder))
            _files = new(settings.SharedFolder, AppSettings.TransferCache, session.PeerId);
        session.CommandHandler = HandleAsync;
        if (session.CanSendScreen) session.StartScreenProducer(ProduceAsync);
    }
    private async Task<RemoteCommand> HandleAsync(RemoteCommand command, CancellationToken token)
    {
        if (_closed) return command.Fail("Session ended.");
        switch (command.Operation)
        {
            case Operation.SetStream:
                if (command.DisableAnimations && !_session.Permissions.DesktopEffects) return command.Fail("Desktop animation permission was not granted.");
                lock (_effectsGate)
                {
                    if (_closed) return command.Fail("Session ended.");
                    try { _effects.SetDisabled(command.DisableAnimations); }
                    catch (Win32Exception) { return command.Fail("Windows refused the temporary animation change."); }
                    _profile = new(command.Fps, command.Width, command.Quality);
                }
                return command.Reply();
            case Operation.RequestInputLock:
                if (!_settings.AllowInputLock || _input is null) return command.Fail("Input blocking is disabled in Host settings.");
                if (!await _confirm(command.Operation)) return command.Fail("Host declined physical-input blocking.");
                bool blocked;
                try { blocked = await _input.BlockPhysicalInputFor30SecondsAsync(); }
                catch (TimeoutException) { return command.Fail("Input worker did not respond; no block confirmation available."); }
                return blocked ? command.Reply() : command.Fail("Windows refused physical-input blocking (elevation may be required), or a block is already active.");
            case Operation.RequestAdmin:
                if (!_settings.AllowAdminRequests) return command.Fail("Admin restart requests are disabled in Host settings.");
                return await _confirm(command.Operation) ? command.Reply() : command.Fail("Host declined the UAC restart request.");
            default:
                return _files is null ? command.Fail("Host has not configured an accessible shared folder.") : await _files.HandleAsync(command, token);
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
