namespace RemoteDesk.Core;

public enum SessionState { Disconnected, Connecting, Authenticating, WaitingForApproval, Connected, Disconnecting }

/// <summary>Only legal state transitions are accepted, including cancellation at every stage.</summary>
public sealed class SessionStateMachine
{
    private readonly object _gate = new();
    private SessionState _state;
    public SessionState State { get { lock (_gate) return _state; } }
    public void MoveTo(SessionState next)
    {
        lock (_gate)
        {
            var valid = (_state, next) switch
            {
                (SessionState.Disconnected, SessionState.Connecting) => true,
                (SessionState.Connecting, SessionState.Authenticating) => true,
                (SessionState.Authenticating, SessionState.WaitingForApproval) => true,
                (SessionState.WaitingForApproval, SessionState.Connected) => true,
                (_, SessionState.Disconnecting) when _state != SessionState.Disconnected && _state != SessionState.Disconnecting => true,
                (SessionState.Disconnecting, SessionState.Disconnected) => true,
                _ => false
            };
            if (!valid) throw new InvalidOperationException($"Illegal transition: {_state} -> {next}");
            _state = next;
        }
    }
}

// Phase one deliberately does not grant screen capture or remote input capabilities.
public sealed record SessionPermissions(bool ViewScreen = false, bool MouseControl = false, bool KeyboardControl = false);
public sealed record ApprovalRequest(string DeviceId, string DisplayName, string Address, string VerificationCode = "", bool ViewScreen = false, bool ControlInput = false, bool FileAccess = false, bool DesktopEffects = false, string? Password = null);
public sealed record AuditEntry(DateTimeOffset Timestamp, string Event, string Detail);

public sealed record SessionGrant(bool ViewScreen = false, bool ControlInput = false, bool FileAccess = false, bool DesktopEffects = false);
