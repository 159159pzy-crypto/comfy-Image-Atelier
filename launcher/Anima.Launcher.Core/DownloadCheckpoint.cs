using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Anima.Launcher.Core;

internal sealed record Aria2Checkpoint(int SchemaVersion, string Sha256, long Size, string SourceUrl,
    long ImportedPrefixLength = 0, string? ImportedPrefixSha256 = null, string? LegacyETag = null, bool EngineStarted = false);

/// <summary>Separates contiguous HTTP data from aria2's possibly sparse, out-of-order data.</summary>
public static class DownloadCheckpoint
{
    public static string AriaData(string destination) => destination + ".aria2.part";
    public static string AriaControl(string destination) => AriaData(destination) + ".aria2";
    public static string AriaState(string destination) => destination + ".aria2.json";

    public static long RetainedBytes(string destination, long size, string sha)
    {
        try
        {
            if (File.Exists(AriaData(destination)))
            {
                var state = Read(destination, size, sha);
                if (state is null) return 0;
                if (File.Exists(AriaControl(destination)))
                    return ReadCompletedPieces(AriaControl(destination), size, new FileInfo(AriaData(destination)).Length);
                // A prepared legacy migration has not yet exposed its file to aria2.
                if (!state.EngineStarted && new FileInfo(AriaData(destination)).Length == state.ImportedPrefixLength)
                    return state.ImportedPrefixLength;
                return 0;
            }
            var part = destination + ".part";
            if (!File.Exists(part) || !File.Exists(part + ".json")) return 0;
            var legacy = JsonFile.Read<ResumeInfo>(part + ".json");
            var length = new FileInfo(part).Length;
            return legacy.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase) && legacy.Size == size && length <= size ? length : 0;
        }
        catch { return 0; }
    }

    // aria2 1.37's HTTP control format v1: network-endian fixed header, then the complete-piece bitmap.
    // In-flight blocks are deliberately not credited: this is a conservative persisted lower bound.
    public static long ReadCompletedPieces(string controlPath, long expectedSize, long availableLength = long.MaxValue)
    {
        using var input = new FileStream(controlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length < 38 || input.Length > 16 * 1024 * 1024) throw new InvalidDataException("下载检查点大小无效。");
        Span<byte> header = stackalloc byte[34];
        input.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != 1 || BinaryPrimitives.ReadUInt32BigEndian(header[2..]) != 0
            || BinaryPrimitives.ReadUInt32BigEndian(header[6..]) != 0)
            throw new InvalidDataException("不支持的下载检查点格式。");
        var pieceLength = BinaryPrimitives.ReadUInt32BigEndian(header[10..]);
        var size = BinaryPrimitives.ReadUInt64BigEndian(header[14..]);
        var bitmapLength = BinaryPrimitives.ReadUInt32BigEndian(header[30..]);
        if (pieceLength == 0 || size != (ulong)expectedSize || expectedSize <= 0)
            throw new InvalidDataException("下载检查点内容身份不符。");
        var pieces = (size + pieceLength - 1) / pieceLength;
        if (bitmapLength != (pieces + 7) / 8 || bitmapLength > 4 * 1024 * 1024 || input.Length < 38L + bitmapLength)
            throw new InvalidDataException("下载检查点位图无效。");
        var bitmap = new byte[bitmapLength];
        input.ReadExactly(bitmap);
        long complete = 0;
        for (ulong index = 0; index < pieces; index++)
            if ((bitmap[index / 8] & (0x80 >> (int)(index % 8))) != 0)
            {
                var amount = (long)Math.Min(pieceLength, size - index * pieceLength);
                if ((long)(index * pieceLength) + amount > availableLength)
                    throw new InvalidDataException("下载检查点引用了尚不存在的数据。");
                complete += amount;
            }
        return complete;
    }

    internal static Aria2Checkpoint? Read(string destination, long size, string sha)
    {
        if (!File.Exists(AriaState(destination))) return null;
        var state = JsonFile.Read<Aria2Checkpoint>(AriaState(destination));
        return state.SchemaVersion == 1 && state.Size == size && state.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase)
            && state.ImportedPrefixLength >= 0 && state.ImportedPrefixLength <= size ? state : null;
    }

    internal static async Task<Aria2Checkpoint> PrepareAsync(DownloadRequest request, string url, CancellationToken ct)
    {
        var destination = request.Destination;
        var data = AriaData(destination);
        var existing = Read(destination, request.Size, request.Sha256);
        if (File.Exists(data))
        {
            if (existing is null) throw new DownloadCheckpointException("并行下载断点身份不符，请在下载任务中清理断点后重试。");
            if (File.Exists(AriaControl(destination)))
            {
                _ = ReadCompletedPieces(AriaControl(destination), request.Size, new FileInfo(data).Length);
                return existing;
            }
            if (existing.EngineStarted)
                throw new DownloadCheckpointException("并行下载控制文件缺失，不能把稀疏文件当作连续断点。请清理此任务断点后重新下载。");
            if (new FileInfo(data).Length != existing.ImportedPrefixLength
                || !await VerifyPrefix(data, existing.ImportedPrefixLength, existing.ImportedPrefixSha256, ct))
                throw new DownloadCheckpointException("旧断点迁移尚未完成且前缀校验不符，已保留文件。");
            return existing;
        }
        var legacyPath = destination + ".part";
        ResumeInfo? legacy = null;
        try { if (File.Exists(legacyPath + ".json")) legacy = JsonFile.Read<ResumeInfo>(legacyPath + ".json"); } catch { }
        long prefix = 0;
        if (legacy is not null && File.Exists(legacyPath) && legacy.Size == request.Size
            && legacy.Sha256.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase) && new FileInfo(legacyPath).Length <= request.Size)
            prefix = new FileInfo(legacyPath).Length;
        var prefixSha = prefix > 0 ? await PrefixHash(legacyPath, prefix, ct) : null;
        var prepared = new Aria2Checkpoint(1, request.Sha256, request.Size, url, prefix, prefixSha, legacy?.ETag);
        // Persist before the same-volume rename. Recovery can distinguish prepared migration from sparse data.
        JsonFile.Write(AriaState(destination), prepared);
        if (prefix > 0) File.Move(legacyPath, data, false);
        else
        {
            if (File.Exists(AriaControl(destination))) File.Delete(AriaControl(destination));
        }
        return prepared;
    }

    internal static async Task RestoreLegacyBeforeFallbackAsync(DownloadRequest request, CancellationToken ct)
    {
        var data = AriaData(request.Destination);
        if (!File.Exists(data)) return;
        var state = Read(request.Destination, request.Size, request.Sha256);
        if (state is null || state.EngineStarted || File.Exists(AriaControl(request.Destination)))
            throw new DownloadCheckpointException("已保留并行下载断点；兼容模式不能安全续写此文件。请恢复并行引擎，或清理断点后重新下载。");
        if (!await VerifyPrefix(data, state.ImportedPrefixLength, state.ImportedPrefixSha256, ct)
            || new FileInfo(data).Length != state.ImportedPrefixLength)
            throw new DownloadCheckpointException("迁移前缀校验失败，已保留断点。");
        var legacy = request.Destination + ".part";
        if (File.Exists(legacy)) throw new DownloadCheckpointException("两种下载断点同时存在，已保留供检查。");
        File.Move(data, legacy);
        JsonFile.Write(legacy + ".json", new ResumeInfo(request.Sha256, request.Size, state.SourceUrl, state.LegacyETag));
        File.Delete(AriaState(request.Destination));
    }

    internal static void MarkStarted(DownloadRequest request, Aria2Checkpoint state) =>
        JsonFile.Write(AriaState(request.Destination), state with { EngineStarted = true });

    private static async Task<string> PrefixHash(string path, long length, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct);
            if (read == 0) throw new InvalidDataException("迁移断点长度不足。");
            hash.AppendData(buffer, 0, read);
            length -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task<bool> VerifyPrefix(string path, long length, string? hash, CancellationToken ct) =>
        length == 0 || hash is not null && (await PrefixHash(path, length, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase);

    internal static void Cleanup(string destination, bool includeLegacy = true)
    {
        var paths = new List<string> { AriaData(destination), AriaControl(destination), AriaControl(destination) + "__temp", AriaState(destination), AriaState(destination) + ".tmp" };
        if (includeLegacy) paths.AddRange([destination + ".part", destination + ".part.json", destination + ".part.json.tmp",
            destination + ".http-restart.part", destination + ".http-restart.part.json", destination + ".http-restart.part.json.tmp"]);
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("下载暂存文件不能是链接。");
        }
        foreach (var path in paths) File.Delete(path);
    }
}
