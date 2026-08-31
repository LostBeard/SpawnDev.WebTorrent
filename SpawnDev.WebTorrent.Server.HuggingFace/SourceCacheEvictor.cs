using System.Collections.Concurrent;

namespace SpawnDev.WebTorrent.Server.HuggingFace;

/// <summary>
/// Keeps the source-proxy cache from filling the drive, evicting least-recently-used entries.
/// </summary>
/// <remarks>
/// <para>
/// The proxy caches whole files and whole archives, and archives are big - listing the fp32 ZipVoice
/// package pulls 634 MB before a single member comes out. Without a bound the cache grows until the drive
/// is full, and then the proxy does not degrade, it FAILS: fetches error, extractions leave nothing usable,
/// and the tracker and web seed sharing that disk fail with it. Re-fetching an evicted file is cheap;
/// running a host out of disk is not.
/// </para>
/// <para>
/// ⚠️ EVICTION IS CHECKED BEFORE A FETCH, not only after. Checking afterwards means the download that
/// fills the drive is the one that succeeds in filling it - the guard fires having already caused the
/// problem it exists to prevent.
/// </para>
/// <para>
/// ⚠️ LRU here does NOT read <c>File.GetLastAccessTimeUtc</c>, and that is deliberate: NTFS last-access
/// updates are disabled by default on Windows (<c>NtfsDisableLastAccessUpdate</c>), so that timestamp can
/// sit unchanged for the life of a file and "least recently used" would silently mean "oldest on disk".
/// Access is tracked in memory instead, seeded from last-WRITE time for anything this process has not
/// served yet - so a restart degrades to write-order rather than to nonsense.
/// </para>
/// </remarks>
public sealed class SourceCacheEvictor
{
    private readonly SourceProxyOptions _options;
    private readonly ConcurrentDictionary<string, DateTime> _lastAccessUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Files younger than this are spared on the first pass - they are the most likely to be in use, and
    /// evicting what was just fetched is how a cache turns into a download loop.
    /// </summary>
    private static readonly TimeSpan RecentGrace = TimeSpan.FromMinutes(2);

    /// <summary>Entries with a fetch or extraction in flight. Never evicted, at any pass.</summary>
    private readonly ConcurrentDictionary<string, int> _pinned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised with the full path of every evicted entry, BEFORE the caller is told room was made.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not optional bookkeeping. A cache that memoises per-file state - which chunks are present, how big
    /// the file is - keeps believing that state after the bytes are deleted underneath it, and then serves a
    /// request by reading a file that is gone. That is a 500, not a cache miss. Anything holding such state
    /// must drop it here.
    /// </remarks>
    public event Action<string>? Evicted;

    public SourceCacheEvictor(SourceProxyOptions options) => _options = options;

    /// <summary>Note that a cache entry was used, so it sorts as recently-used.</summary>
    public void Touch(string path) => _lastAccessUtc[Norm(path)] = DateTime.UtcNow;

    /// <summary>
    /// Every path this class handles is normalised to a full path first.
    /// </summary>
    /// <remarks>
    /// ⚠️ The configured cache directory is RELATIVE in the deployed config ("src-cache"), so a path built
    /// by combining it does not equal the absolute path that directory enumeration reports for the very same
    /// file. Comparing the two forms without normalising fails silently in exactly the worst way: every pin
    /// and every touch is recorded under a key nothing ever looks up, so the guards appear to work while
    /// protecting nothing.
    /// </remarks>
    private static string Norm(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>
    /// Protect an entry for as long as the returned handle lives - a file being written RIGHT NOW must not
    /// become a victim.
    /// </summary>
    /// <remarks>
    /// ⚠️ File locking is not a substitute for this, and assuming it was would be a Windows-only assumption.
    /// On Windows an open file refuses to delete, so eviction would merely fail noisily. On Linux - where
    /// this hub runs - the delete SUCCEEDS: the name goes away while the writer keeps filling an unlinked
    /// inode, so the fetch reports success and leaves nothing behind. Pinning makes the guarantee explicit
    /// rather than borrowing it from whichever filesystem happens to be underneath.
    /// </remarks>
    public IDisposable Pin(string path)
    {
        path = Norm(path);
        _pinned.AddOrUpdate(path, 1, (_, n) => n + 1);
        return new PinHandle(this, path);
    }

    private void Unpin(string path)
    {
        // Refcounted: two requests can want the same archive at once, and the first to finish must not
        // unprotect it for the second.
        while (_pinned.TryGetValue(path, out var n))
        {
            if (n <= 1) { if (_pinned.TryRemove(new KeyValuePair<string, int>(path, n))) return; }
            else if (_pinned.TryUpdate(path, n - 1, n)) return;
        }
    }

    private sealed class PinHandle : IDisposable
    {
        private readonly SourceCacheEvictor _owner;
        private readonly string _path;
        private int _disposed;
        public PinHandle(SourceCacheEvictor owner, string path) { _owner = owner; _path = path; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Unpin(_path);
        }
    }

    /// <summary>Total bytes currently held in the cache directory.</summary>
    public long CurrentSizeBytes()
    {
        try
        {
            return EnumerateCacheFiles().Sum(f => f.Length);
        }
        catch { return 0; }
    }

    /// <summary>Free bytes on the cache drive, or long.MaxValue when it cannot be determined.</summary>
    public long FreeSpaceBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_options.CacheDirectory));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return long.MaxValue; }   // network paths and odd mounts: do not evict on a guess
    }

    /// <summary>
    /// Make room for an incoming fetch, evicting least-recently-used entries as needed.
    /// </summary>
    /// <param name="incomingBytes">
    /// Size of what is about to be fetched, when known (0 when not). Counted against the free-space floor
    /// so the guard accounts for the download that has not happened yet rather than the one that already
    /// filled the disk.
    /// </param>
    /// <returns>False when even a full eviction cannot make room - the caller should refuse rather than
    /// start a fetch that will fail partway and leave a truncated file behind.</returns>
    public async Task<bool> EnsureRoomAsync(long incomingBytes = 0)
    {
        await _gate.WaitAsync();
        try
        {
            var evicted = 0;
            long freedBytes = 0;

            while (true)
            {
                long size = CurrentSizeBytes();
                long free = FreeSpaceBytes();

                bool overSizeLimit = _options.MaxCacheSizeBytes > 0
                                     && size + incomingBytes > _options.MaxCacheSizeBytes;
                bool underFreeFloor = free - incomingBytes < _options.MinFreeDiskSpaceBytes;
                if (!overSizeLimit && !underFreeFloor) break;

                // Pass 1 spares recently-written entries; pass 2 does not. Refusing a fetch while evictable
                // data sits on the disk would be the guard causing the outage it exists to prevent, so the
                // grace period yields before the drive does. Pinned entries are spared in BOTH passes.
                var victim = SelectVictim(sparingRecent: true) ?? SelectVictim(sparingRecent: false);
                if (victim == null)
                {
                    // Nothing left that is safe to remove. Say so loudly: silently proceeding would start a
                    // fetch that cannot finish.
                    Console.WriteLine($"[SourceProxy] cannot make room for {incomingBytes / 1048576} MB - "
                        + $"cache {size / 1048576} MB, free {free / 1048576} MB, floor "
                        + $"{_options.MinFreeDiskSpaceBytes / 1048576} MB, and nothing evictable remains");
                    return false;
                }

                freedBytes += DeleteEntry(victim);
                evicted++;
            }

            if (evicted > 0)
                Console.WriteLine($"[SourceProxy] evicted {evicted} entr{(evicted == 1 ? "y" : "ies")}, "
                    + $"{freedBytes / 1048576} MB freed; cache now {CurrentSizeBytes() / 1048576} MB, "
                    + $"{FreeSpaceBytes() / 1048576} MB free");
            return true;
        }
        finally { _gate.Release(); }
    }

    private IEnumerable<FileInfo> EnumerateCacheFiles()
    {
        if (!Directory.Exists(_options.CacheDirectory)) return Array.Empty<FileInfo>();
        return new DirectoryInfo(_options.CacheDirectory)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            // A .partial is an extraction in flight and a .manifest belongs to its data file - neither is
            // an independent eviction candidate.
            .Where(f => !f.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                     && !f.Name.EndsWith(PartialFileCache.ManifestSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The least-recently-used entry that is safe to delete, or null when there is none.</summary>
    private FileInfo? SelectVictim(bool sparingRecent)
    {
        var now = DateTime.UtcNow;
        return EnumerateCacheFiles()
            .Where(f => !_pinned.ContainsKey(f.FullName))
            .Where(f => !sparingRecent || now - f.LastWriteTimeUtc > RecentGrace)
            .OrderBy(f => _lastAccessUtc.TryGetValue(f.FullName, out var t) ? t : f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private long DeleteEntry(FileInfo file)
    {
        long size = 0;
        try
        {
            size = file.Length;
            file.Delete();
            _lastAccessUtc.TryRemove(file.FullName, out _);
            // The manifest records which chunks are present; orphaning it would make a re-fetch believe it
            // already had data it no longer has.
            var manifest = file.FullName + PartialFileCache.ManifestSuffix;
            if (File.Exists(manifest)) File.Delete(manifest);
            // A cached member listing describes THIS archive; keeping it after the archive is gone leaves a
            // listing whose members cannot be fetched.
            var listing = file.FullName + SourceProxy.ListingSuffix;
            if (File.Exists(listing)) File.Delete(listing);
            Evicted?.Invoke(file.FullName);
            Console.WriteLine($"[SourceProxy] evicted {file.Name} ({size / 1048576} MB)");
        }
        catch (Exception ex)
        {
            // A file held open by an in-flight response cannot be deleted; skip it rather than aborting the
            // whole eviction pass, but remember it so the loop does not pick it again forever.
            Console.WriteLine($"[SourceProxy] could not evict {file.Name}: {ex.GetType().Name}");
            _lastAccessUtc[file.FullName] = DateTime.UtcNow;
            return 0;
        }
        return size;
    }
}
