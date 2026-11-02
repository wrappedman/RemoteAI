using RemoteDesk.Core;
var state = new SessionStateMachine();
foreach (var next in new[] { SessionState.Connecting, SessionState.Authenticating, SessionState.WaitingForApproval, SessionState.Connected, SessionState.Disconnecting, SessionState.Disconnected }) state.MoveTo(next);
try { state.MoveTo(SessionState.Connected); throw new Exception("Illegal transition accepted"); }
catch (InvalidOperationException) { }
foreach (var stage in new[] { 1, 2, 3, 4 })
{
    var s = new SessionStateMachine();
    foreach (var next in new[] { SessionState.Connecting, SessionState.Authenticating, SessionState.WaitingForApproval, SessionState.Connected }.Take(stage)) s.MoveTo(next);
    s.MoveTo(SessionState.Disconnecting); s.MoveTo(SessionState.Disconnected);
}
var permissions = new SessionPermissions();
if (permissions.ViewScreen || permissions.KeyboardControl || permissions.MouseControl) throw new Exception("Unsafe permissions");
Console.WriteLine("PASS: legal lifecycle, invalid transitions, cancellation at every stage, default-deny permissions");
