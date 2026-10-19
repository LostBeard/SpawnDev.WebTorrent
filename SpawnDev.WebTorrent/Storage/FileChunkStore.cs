namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// File-based chunk store for desktop .NET (<see cref="WebTorrentClient.StorageFolder"/>).
/// Stores each piece as a separate file: {baseDir}/piece_{index:D6}.bin
///
/// A piece is written to a temporary file and moved into place, so a crash or a concurrent reader can never see a half-written piece file (the bitfield promises that a stored
/// piece can be served: a torn file would break that promise far from the cause). A range read opens the file and reads just the range, so a 4 MB read that touches 16 pieces of
/// 256 KB does not load every piece whole first.
///
/// For browser (Blazor WASM), use an IAsyncFS-backed implementation
/// (SpawnDev.SpawnJS.AsyncFileSystem) or MemoryChunkStore.
/// </summary>
public class FileChunkStore : IChunkStore
{
    private readonly string _baseDir;
    public int ChunkLength { get; }

    public FileChunkStore(string baseDir, int chunkLength)
    {
        _baseDir = baseDir;
        ChunkLength = chunkLength;
        Directory.CreateDirectory(baseDir);
    }

    public async Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var path = GetPath(index);
        // a unique temporary name: two writers of the same piece (two peers delivering it) must not share one
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, ct).ConfigureAwait(false);
            // On Windows replacing a file a reader has open fails while the reader holds it (readers here open with FileShare.Delete, but another process or an older reader may not): retry briefly
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, overwrite: true); break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 50) { await Task.Delay(10, ct).ConfigureAwait(false); }
            }
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    public async Task<byte[]?> GetAsync(int index, CancellationToken ct = default)
    {
        var path = GetPath(index);
        try
        {
            // FileShare.Delete: a writer may replace this piece (atomically, by moving a finished file over it) while it is being read
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var result = new byte[stream.Length];
            int read = 0;
            while (read < result.Length)
            {
                int got = await stream.ReadAsync(result.AsMemory(read), ct).ConfigureAwait(false);
                if (got == 0) return null;
                read += got;
            }
            return result;
        }
        catch (FileNotFoundException) { return null; } // not stored (or removed between the open and the read)
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        var path = GetPath(index);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
            if (offset < 0 || length < 0 || (long)offset + length > stream.Length) return null;
            stream.Position = offset;
            var result = new byte[length];
            int read = 0;
            while (read < length)
            {
                int got = await stream.ReadAsync(result.AsMemory(read, length - read), ct).ConfigureAwait(false);
                if (got == 0) return null;
                read += got;
            }
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public Task RemoveAsync(int index, CancellationToken ct = default)
    {
        var path = GetPath(index);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (Directory.Exists(_baseDir))
        {
            foreach (var file in Directory.GetFiles(_baseDir, "piece_*.bin"))
                File.Delete(file);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Files persist — nothing to clean up
        return ValueTask.CompletedTask;
    }

    private string GetPath(int index) => Path.Combine(_baseDir, $"piece_{index:D6}.bin");
}
