using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RemoteDesk.Protocol;
namespace RemoteDesk.Windows;

/// <summary>One dedicated thread owns injection and any short, explicitly consented physical-input block.</summary>
public sealed class InputController : IDisposable
{
    private sealed record Work(InputEvent? Input, TaskCompletionSource<bool>? Lock);
    private readonly BlockingCollection<Work> _queue = new(256);
    private readonly Thread _thread;
    private readonly HashSet<int> _keys = [];
    private readonly HashSet<int> _buttons = [];
    private int _disposed;
    public event Action<string>? Status;
    public InputController()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RemoteDesk approved input" }; _thread.Start();
    }
    public void Post(InputEvent input)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { if (!_queue.TryAdd(new(input, null))) Status?.Invoke("Input queue full; release control and retry."); }
        catch (InvalidOperationException) { }
    }
    public async Task<bool> BlockPhysicalInputFor30SecondsAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { if (!_queue.TryAdd(new(null, completion))) return false; }
        catch (InvalidOperationException) { return false; }
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private void Run()
    {
        long unlockAt = 0;
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && !_queue.IsCompleted)
            {
                if (unlockAt != 0 && Environment.TickCount64 >= unlockAt)
                { _ = BlockInput(false); unlockAt = 0; Status?.Invoke("Physical input block expired (30 seconds maximum)."); }
                if (!_queue.TryTake(out var work, 25)) continue;
                if (work.Lock is not null)
                {
                    bool ok = unlockAt == 0 && BlockInput(true);
                    if (ok) unlockAt = Environment.TickCount64 + 30000;
                    work.Lock.TrySetResult(ok); continue;
                }
                Apply(work.Input!);
            }
        }
        finally
        {
            _ = BlockInput(false); ReleaseAll();
            while (_queue.TryTake(out var pending)) pending.Lock?.TrySetResult(false);
        }
    }
    private void Apply(InputEvent e)
    {
        if (e.Kind == InputKind.ReleaseAll) { ReleaseAll(); return; }
        if (e.Kind == InputKind.Key)
        {
            // Track remote-owned keys only, always release them on disconnect/focus loss.
            if (e.IsUp) _keys.Remove(e.Code); else _keys.Add(e.Code);
            var input = new INPUT { Type = 1, Union = new INPUTUNION { Keyboard = new KEYBDINPUT
            { Vk = (ushort)e.Code, Flags = (e.IsUp ? 2u : 0) | (IsExtended(e.Code) ? 1u : 0) } } };
            Send(input); return;
        }
        Send(new INPUT { Union = new INPUTUNION { Mouse = new MOUSEINPUT { X = e.X, Y = e.Y, Flags = 0x8001 } } });
        if (e.Kind == InputKind.Button)
        {
            if (e.IsUp) _buttons.Remove(e.Code); else _buttons.Add(e.Code);
            uint flags = e.Code switch { 1 => e.IsUp ? 4u : 2u, 2 => e.IsUp ? 16u : 8u, 3 => e.IsUp ? 64u : 32u, _ => e.IsUp ? 0x100u : 0x80u };
            Send(new INPUT { Union = new INPUTUNION { Mouse = new MOUSEINPUT { Flags = flags, Data = e.Code > 3 ? (uint)(e.Code - 3) : 0 } } });
        }
        if (e.Kind == InputKind.Wheel)
            Send(new INPUT { Union = new INPUTUNION { Mouse = new MOUSEINPUT { Flags = 0x800, Data = unchecked((uint)e.Delta) } } });
    }
    private static bool IsExtended(int key) => key is 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 45 or 46 or 91 or 92 or 111 or 163 or 165;
    private void ReleaseAll()
    {
        foreach (int key in _keys.ToArray()) Send(new INPUT { Type = 1, Union = new INPUTUNION { Keyboard = new KEYBDINPUT { Vk = (ushort)key, Flags = 2u | (IsExtended(key) ? 1u : 0) } } });
        foreach (int button in _buttons.ToArray())
        {
            uint flags = button switch { 1 => 4u, 2 => 16u, 3 => 64u, _ => 0x100u };
            Send(new INPUT { Union = new INPUTUNION { Mouse = new MOUSEINPUT { Flags = flags, Data = button > 3 ? (uint)(button - 3) : 0 } } });
        }
        _keys.Clear(); _buttons.Clear();
    }
    private void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) Status?.Invoke("Windows blocked input (UIPI/UAC, secure desktop or session boundary).");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding(); _thread.Join(); _queue.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Union; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool block);
}
