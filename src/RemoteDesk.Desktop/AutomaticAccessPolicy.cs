using RemoteDesk.Core;
using RemoteDesk.Windows;
namespace RemoteDesk.Desktop;

public enum AutomaticAccessResult { Approved, Disabled, UntrustedKey, InvalidPassword, Cooldown }

/// <summary>App-open password approval, restricted to explicitly paired full Controller keys.</summary>
public sealed class AutomaticAccessPolicy(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private int _failures;
    private long _blockedUntil;
    public void Reset() { Interlocked.Exchange(ref _failures, 0); Interlocked.Exchange(ref _blockedUntil, 0); }
    public async Task<AutomaticAccessResult> EvaluateAsync(AppSettings settings, ApprovalRequest request, CancellationToken token)
    {
        if (!settings.PasswordAccess || string.IsNullOrEmpty(settings.ProtectedPasswordVerifier) || string.IsNullOrEmpty(request.Password))
            return AutomaticAccessResult.Disabled;
        if (_clock() < Interlocked.Read(ref _blockedUntil)) return AutomaticAccessResult.Cooldown;
        bool trusted = settings.TrustedControllers.Contains(request.DeviceId, StringComparer.Ordinal);
        bool valid = trusted && await Task.Run(() => PasswordVerifier.Verify(settings.ProtectedPasswordVerifier, request.Password), token);
        token.ThrowIfCancellationRequested();
        if (valid) { Interlocked.Exchange(ref _failures, 0); return AutomaticAccessResult.Approved; }
        if (Interlocked.Increment(ref _failures) >= 3)
        {
            Interlocked.Exchange(ref _blockedUntil, _clock() + 60000);
            Interlocked.Exchange(ref _failures, 0);
        }
        return trusted ? AutomaticAccessResult.InvalidPassword : AutomaticAccessResult.UntrustedKey;
    }
}
