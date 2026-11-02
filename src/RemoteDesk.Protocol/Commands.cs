namespace RemoteDesk.Protocol;

public enum InputKind { Move, Button, Wheel, Key, ReleaseAll }
public sealed record InputEvent(InputKind Kind, int X = 0, int Y = 0, int Code = 0, bool IsUp = false, int Delta = 0);
public enum Operation { ListFiles, ReadInfo, ReadChunk, WriteBegin, WriteChunk, WriteComplete, SetStream, RequestAdmin, RequestInputLock, ShareInfo }
public sealed record FileEntry(string Name, bool Directory, long Size);
public sealed record RemoteCommand(Guid Id, Operation Operation, string? Path = null, byte[]? Data = null,
    long Offset = 0, long Length = 0, string? Hash = null, FileEntry[]? Entries = null, string? Error = null,
    int Fps = 20, int Width = 1440, int Quality = 55, bool DisableAnimations = false, bool Reset = false)
{
    public static RemoteCommand Create(Operation operation, string? path = null) => new(Guid.NewGuid(), operation, path);
    public RemoteCommand Fail(string error) => new(Id, Operation, Error: error);
    public RemoteCommand Reply() => new(Id, Operation);
}
