using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDesk.Protocol;

namespace RemoteDesk.Files;

/// <summary>Only files under an explicitly chosen root; no remote shell, arbitrary paths or overwrite.</summary>
public sealed class SharedFolderService
{
    public const long MaxFileSize = 8L * 1024 * 1024 * 1024;
    private readonly string _root, _cache, _peer;
    private sealed record Upload(string Name, long Size, string Hash);
    public SharedFolderService(string root, string cache, string peer)
    {
        _root = Path.GetFullPath(root); _cache = Path.GetFullPath(cache);
        _peer = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peer)));
        ValidateRoot(_root); Directory.CreateDirectory(_cache); RejectLinks(_cache);
    }
    public static void ValidateRoot(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose an existing shared folder in Settings.");
        RejectLinks(root);
    }
    private static void RejectLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if (File.Exists(p) || Directory.Exists(p))
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Symbolic links/reparse points are not shared.");
    }
    private string SafePath(string? relative)
    {
        relative ??= "";
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') || relative.Split('/').Any(p => p is "." or ".." || p.StartsWith(".remotedesk-", StringComparison.OrdinalIgnoreCase) || p.EndsWith('.') || p.EndsWith(' ') || System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new UnauthorizedAccessException("Invalid shared path.");
        string full = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path leaves the shared folder.");
        RejectLinks(full); return full;
    }
    private string Part(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid content hash.");
        return Path.Combine(_cache, _peer + "-" + hash.ToUpperInvariant() + ".part");
    }
    public static async Task<string> HashAsync(string path, CancellationToken token, long? limit = null)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (limit is null) return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[65536]; long remaining = limit.Value;
        while (remaining > 0)
        {
            int n = await input.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, remaining)), token);
            if (n == 0) throw new EndOfStreamException(); hash.AppendData(bytes, 0, n); remaining -= n;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public async Task<RemoteCommand> HandleAsync(RemoteCommand c, CancellationToken token)
    {
        switch (c.Operation)
        {
            case Operation.ShareInfo:
                return c.Reply() with { Path = _root };
            case Operation.ListFiles:
                string dir = SafePath(c.Path);
                var entries = Directory.EnumerateFileSystemEntries(dir).Where(p => !Path.GetFileName(p).StartsWith(".remotedesk-", StringComparison.OrdinalIgnoreCase)).Take(257).ToArray();
                if (entries.Length > 256) return c.Fail("This folder has more than 256 entries. Share a smaller subfolder.");
                var items = new List<FileEntry>();
                foreach (string path in entries)
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    bool isDir = Directory.Exists(path); items.Add(new(Path.GetFileName(path), isDir, isDir ? 0 : new FileInfo(path).Length));
                }
                return c.Reply() with { Entries = items.OrderByDescending(e => e.Directory).ThenBy(e => e.Name).ToArray() };
            case Operation.ReadInfo:
                string file = SafePath(c.Path); long length = new FileInfo(file).Length;
                if (length > MaxFileSize) return c.Fail("File exceeds 8 GiB limit.");
                return c.Reply() with { Length = length, Hash = await HashAsync(file, token) };
            case Operation.ReadChunk:
                string read = SafePath(c.Path);
                await using (var stream = new FileStream(read, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true))
                {
                    if (stream.Length > MaxFileSize || c.Offset > stream.Length) return c.Fail("Invalid file range.");
                    stream.Position = c.Offset; byte[] bytes = new byte[(int)Math.Min(32768, stream.Length - c.Offset)];
                    await stream.ReadExactlyAsync(bytes, token); return c.Reply() with { Data = bytes, Offset = c.Offset };
                }
            case Operation.WriteBegin:
                if (string.IsNullOrWhiteSpace(c.Path) || Path.GetFileName(c.Path) != c.Path || c.Path.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || c.Hash is null)
                    return c.Fail("Uploads need a plain filename, size and SHA-256 hash.");
                string destination = SafePath(c.Path);
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    if (File.Exists(destination) && new FileInfo(destination).Length == c.Length && await HashAsync(destination, token) == c.Hash)
                        return c.Reply() with { Offset = c.Length, Hash = c.Hash, Reset = true };
                    return c.Fail("Destination already exists. Rename the source; existing files are never overwritten.");
                }
                string part = Part(c.Hash); RejectLinks(part);
                string meta = part + ".json"; RejectLinks(meta);
                var offer = new Upload(c.Path, c.Length, c.Hash);
                if (File.Exists(meta))
                {
                    var previous = JsonSerializer.Deserialize<Upload>(await File.ReadAllTextAsync(meta, token));
                    if (previous != offer) return c.Fail("Resume metadata does not match this offer.");
                }
                else
                {
                    if (Directory.EnumerateFiles(_cache, "*.part").Take(16).Count() >= 16) return c.Fail("Partial-transfer limit reached. Clean the cache locally in Settings.");
                    if (Directory.EnumerateFiles(_cache, "*.part").Sum(f => new FileInfo(f).Length) + c.Length > 16L * 1024 * 1024 * 1024)
                        return c.Fail("Partial-transfer disk quota exceeded.");
                    await File.WriteAllTextAsync(meta, JsonSerializer.Serialize(offer), token);
                }
                if (c.Reset || !File.Exists(part))
                { await using var create = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1, true); }
                long offset = new FileInfo(part).Length;
                if (offset > c.Length) return c.Fail("Invalid partial file; retry with reset.");
                return c.Reply() with { Offset = offset, Hash = await HashAsync(part, token, offset) };
            case Operation.WriteChunk:
                if (c.Hash is null || c.Data is not { Length: > 0 }) return c.Fail("Missing chunk.");
                if (Directory.EnumerateFiles(_cache, "*.part").Sum(f => new FileInfo(f).Length) + c.Data.Length > 16L * 1024 * 1024 * 1024)
                    return c.Fail("Partial-transfer disk quota reached. Clean completed/unneeded parts locally.");
                string chunkPath = Part(c.Hash); RejectLinks(chunkPath); RejectLinks(chunkPath + ".json");
                var upload = JsonSerializer.Deserialize<Upload>(await File.ReadAllTextAsync(chunkPath + ".json", token)) ?? throw new InvalidDataException();
                await using (var output = new FileStream(chunkPath, FileMode.Open, FileAccess.Write, FileShare.None, 32768, true))
                {
                    if (output.Length != c.Offset || c.Offset + c.Data.Length > upload.Size) return c.Fail("Chunk offset mismatch.");
                    output.Position = c.Offset; await output.WriteAsync(c.Data, token); await output.FlushAsync(token);
                    return c.Reply() with { Offset = output.Position };
                }
            case Operation.WriteComplete:
                if (c.Hash is null) return c.Fail("Missing content hash.");
                string finished = Part(c.Hash); RejectLinks(finished); RejectLinks(finished + ".json");
                var metadata = JsonSerializer.Deserialize<Upload>(await File.ReadAllTextAsync(finished + ".json", token)) ?? throw new InvalidDataException();
                if (new FileInfo(finished).Length != metadata.Size || await HashAsync(finished, token) != metadata.Hash)
                    return c.Fail("Final SHA-256 failed. Resume/reset and retry.");
                string final = SafePath(metadata.Name);
                string staged = Path.Combine(_root, ".remotedesk-" + _peer + "-" + c.Hash + ".incoming");
                RejectLinks(staged);
                // Stage on the destination volume, then rename without overwrite. A cancelled
                // cross-volume commit never exposes an unverified partial under the final name.
                await using (var source = new FileStream(finished, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var target = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                { await source.CopyToAsync(target, token); await target.FlushAsync(token); }
                if (await HashAsync(staged, token) != metadata.Hash) return c.Fail("Destination staging hash failed.");
                File.Move(staged, final, false); File.Delete(finished); File.Delete(finished + ".json");
                return c.Reply() with { Offset = metadata.Size };
            default: return c.Fail("Not a file operation.");
        }
    }
}
