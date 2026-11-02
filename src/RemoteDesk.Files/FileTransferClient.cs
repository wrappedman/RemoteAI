using RemoteDesk.Protocol;
using RemoteDesk.Transport;
namespace RemoteDesk.Files;

public static class FileTransferClient
{
    public static async Task UploadAsync(SecureSession session, string source, IProgress<string>? progress, CancellationToken token)
    {
        long length = new FileInfo(source).Length;
        if (length > SharedFolderService.MaxFileSize) throw new IOException("Maximum file size is 8 GiB.");
        progress?.Report("Hashing source for safe resume…"); string hash = await SharedFolderService.HashAsync(source, token);
        var begin = RemoteCommand.Create(Operation.WriteBegin, Path.GetFileName(source)) with { Length = length, Hash = hash };
        var response = await session.RequestAsync(begin, token);
        long offset = response.Offset;
        if (offset < 0 || offset > length) throw new InvalidDataException("Invalid resume offset.");
        if (offset < length && await SharedFolderService.HashAsync(source, token, offset) != response.Hash)
        {
            response = await session.RequestAsync(begin with { Id = Guid.NewGuid(), Reset = true }, token); offset = response.Offset;
            if (offset != 0) throw new InvalidDataException("Resume reset failed.");
        }
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
        input.Position = offset;
        while (offset < length)
        {
            byte[] bytes = new byte[(int)Math.Min(32768, length - offset)]; await input.ReadExactlyAsync(bytes, token);
            var ack = await session.RequestAsync(RemoteCommand.Create(Operation.WriteChunk) with { Hash = hash, Offset = offset, Data = bytes }, token);
            if (ack.Offset != offset + bytes.Length) throw new InvalidDataException("Chunk was not acknowledged.");
            offset = ack.Offset; progress?.Report($"Uploading {offset:N0} / {length:N0} bytes (resume supported)");
        }
        if (!response.Reset)
            await session.RequestAsync(RemoteCommand.Create(Operation.WriteComplete) with { Hash = hash }, token);
        progress?.Report("Upload complete; destination verified with SHA-256.");
    }
    public static async Task DownloadAsync(SecureSession session, string remote, string destination, IProgress<string>? progress, CancellationToken token)
    {
        var info = await session.RequestAsync(RemoteCommand.Create(Operation.ReadInfo, remote), token);
        if (info.Hash is null || info.Length > SharedFolderService.MaxFileSize || File.Exists(destination)) throw new IOException("Choose a new destination filename.");
        string part = destination + ".remotedesk.part"; string metadata = part + ".json";
        string identity = $"{session.PeerId}|{remote}|{info.Length}|{info.Hash}";
        if (File.Exists(metadata) && await File.ReadAllTextAsync(metadata, token) != identity) throw new IOException("A different partial download uses that filename.");
        if (File.Exists(part) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked partial paths are not permitted.");
        if (File.Exists(metadata) && (File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked metadata is not permitted.");
        if (!File.Exists(metadata)) { await File.WriteAllTextAsync(metadata, identity, token); if (File.Exists(part)) throw new IOException("An unrecognized partial file exists. Remove it locally before retrying."); }
        await using (var output = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 32768, true))
        {
            if (output.Length > info.Length) throw new IOException("Invalid partial size.");
            output.Position = output.Length;
            while (output.Position < info.Length)
            {
                var chunk = await session.RequestAsync(RemoteCommand.Create(Operation.ReadChunk, remote) with { Offset = output.Position }, token);
                if (chunk.Offset != output.Position || chunk.Data is not { Length: > 0 } || output.Position + chunk.Data.Length > info.Length)
                    throw new InvalidDataException("Invalid download chunk.");
                await output.WriteAsync(chunk.Data, token); await output.FlushAsync(token);
                progress?.Report($"Downloading {output.Position:N0} / {info.Length:N0} bytes (resume supported)");
            }
        }
        if (await SharedFolderService.HashAsync(part, token) != info.Hash) throw new IOException("Downloaded content changed or failed SHA-256. Remove the partial file and retry.");
        File.Move(part, destination, false); File.Delete(metadata); progress?.Report("Download complete; SHA-256 verified.");
    }
}
