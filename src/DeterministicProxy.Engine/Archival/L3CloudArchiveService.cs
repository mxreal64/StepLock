using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Engine.Archival;

public sealed record ArchiveManifest(
    string SessionId,
    string BranchId,
    int TotalFrames,
    long TotalBytes,
    string RootMerkleHash,
    string ArchiveSha256,
    DateTimeOffset CreatedAtUtc
);

public interface ICloudStorageProvider
{
    Task UploadAsync(string key, Stream dataStream, CancellationToken ct = default);
    Task<Stream?> DownloadAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

public sealed class InMemoryCloudStorageProvider : ICloudStorageProvider
{
    private readonly Dictionary<string, byte[]> _storage = new();

    public Task UploadAsync(string key, Stream dataStream, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        dataStream.CopyTo(ms);
        _storage[key] = ms.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream?> DownloadAsync(string key, CancellationToken ct = default)
    {
        if (_storage.TryGetValue(key, out var bytes))
        {
            return Task.FromResult<Stream?>(new MemoryStream(bytes));
        }
        return Task.FromResult<Stream?>(null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(_storage.ContainsKey(key));
    }
}

public interface IL3CloudArchiveService
{
    Task<ArchiveManifest> ExportSessionToCloudAsync(string sessionId, string branchId, CancellationToken ct = default);
    Task<int> ImportSessionFromCloudAsync(string sessionId, string branchId, CancellationToken ct = default);
}

public sealed class L3CloudArchiveService : IL3CloudArchiveService
{
    private readonly IExecutionStore _store;
    private readonly ICloudStorageProvider _cloudProvider;

    public L3CloudArchiveService(IExecutionStore store, ICloudStorageProvider cloudProvider)
    {
        _store = store;
        _cloudProvider = cloudProvider;
    }

    public async Task<ArchiveManifest> ExportSessionToCloudAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        var frames = await _store.GetExecutionHistoryAsync(sessionId, branchId, ct);
        if (frames.Count == 0)
        {
            throw new InvalidOperationException($"No execution history found for session '{sessionId}', branch '{branchId}'.");
        }

        var rootHash = frames[0].FrameHash;
        var jsonPayload = JsonSerializer.Serialize(frames);
        var rawBytes = Encoding.UTF8.GetBytes(jsonPayload);

        // Compress using Deflate / GZip into .dpz bundle
        using var compressedStream = new MemoryStream();
        using (var gzip = new GZipStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzip.WriteAsync(rawBytes, ct);
        }
        compressedStream.Position = 0;

        var archiveHash = Convert.ToHexString(SHA256.HashData(compressedStream.ToArray())).ToLowerInvariant();
        compressedStream.Position = 0;

        var storageKey = $"archives/{sessionId}/{branchId}/{archiveHash}.dpz";
        await _cloudProvider.UploadAsync(storageKey, compressedStream, ct);

        var manifest = new ArchiveManifest(
            SessionId: sessionId,
            BranchId: branchId,
            TotalFrames: frames.Count,
            TotalBytes: rawBytes.Length,
            RootMerkleHash: rootHash,
            ArchiveSha256: archiveHash,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );

        var manifestJson = JsonSerializer.Serialize(manifest);
        using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        await _cloudProvider.UploadAsync($"archives/{sessionId}/{branchId}/manifest.json", manifestStream, ct);

        return manifest;
    }

    public async Task<int> ImportSessionFromCloudAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        var manifestStream = await _cloudProvider.DownloadAsync($"archives/{sessionId}/{branchId}/manifest.json", ct);
        if (manifestStream == null)
        {
            throw new FileNotFoundException($"Manifest not found for session '{sessionId}', branch '{branchId}'.");
        }

        using var reader = new StreamReader(manifestStream);
        var manifestJson = await reader.ReadToEndAsync(ct);
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(manifestJson);
        if (manifest == null) throw new InvalidDataException("Invalid manifest payload.");

        var storageKey = $"archives/{sessionId}/{branchId}/{manifest.ArchiveSha256}.dpz";
        var archiveStream = await _cloudProvider.DownloadAsync(storageKey, ct);
        if (archiveStream == null)
        {
            throw new FileNotFoundException($"Archive bundle {storageKey} not found in cloud storage.");
        }

        using var decompressedStream = new MemoryStream();
        using (var gzip = new GZipStream(archiveStream, CompressionMode.Decompress))
        {
            await gzip.CopyToAsync(decompressedStream, ct);
        }

        var framesJson = Encoding.UTF8.GetString(decompressedStream.ToArray());
        var frames = JsonSerializer.Deserialize<List<ExecutionFrame>>(framesJson);
        if (frames == null || frames.Count == 0) return 0;

        await _store.SaveFramesBatchAsync(frames, ct);
        return frames.Count;
    }
}
