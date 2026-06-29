using System.Net.Http.Headers;

namespace SpawnDev.WebTorrent;

public partial class Torrent
{
    /// <summary>
    /// True while this torrent was added from a web-seed URL with the infohash NOT yet known. In this mode pieces
    /// are hash-COMPUTED as they arrive (the first downloader trusts the web seed) instead of being verified
    /// against an expected hash, and the real infohash is finalized when the download completes. Cleared on
    /// finalize. See <see cref="InitLazyHashAsync"/> / <see cref="FinalizeLazyHash"/> and Plans/lazy-hash-torrents.md.
    /// </summary>
    public bool LazyHash { get; private set; }

    /// <summary>The source web-seed URL a Lazy-Hash torrent was created from.</summary>
    public string? LazyUrl { get; private set; }

    private string? _lazyPersistKey;

    /// <summary>
    /// Storage key for the OPFS store dir (<c>webtorrent/{key}</c>) AND the <c>_state/{key}.torrent</c> file. Lazy
    /// torrents key on a stable provisional URL-derived id — their infohash is unknown at add and must NOT relocate
    /// the store on finalize; every other torrent keys on <see cref="WireInfoHashHex"/>. Persist and restore both
    /// use this, so the store dir and the state filename always agree (no migration needed).
    /// </summary>
    internal string PersistKey => !string.IsNullOrEmpty(_lazyPersistKey) ? _lazyPersistKey : WireInfoHashHex;

    /// <summary>Restore calls this to re-key a restored torrent's store to the dir it was persisted under (the
    /// <c>_state</c> filename IS the persist key — see <c>WebTorrentClient.RestoreFromStorageAsync</c>).</summary>
    internal void SetPersistKey(string key) => _lazyPersistKey = key;

    /// <summary>Deterministic provisional storage key for a Lazy-Hash torrent, derived from its source URL so the
    /// same URL always restores to the same OPFS dir across reloads.</summary>
    internal static string LazyProvisionalKey(string url) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes("lazy:" + url))).ToLowerInvariant();

    /// <summary>Fired once a Lazy-Hash torrent's real infohash has been computed (i.e. the download completed and
    /// the torrent is now a normal, identifiable, seedable torrent).</summary>
    public event Action? OnLazyFinalized;

    /// <summary>
    /// Initialize a Lazy-Hash torrent from an http(s) web-seed URL: probe the file size, build a shell with the
    /// right piece geometry but ZEROED piece hashes + the URL as a BEP 19 web seed, then begin downloading. The
    /// name + web-seed + piece-length derivation mirror the eager <see cref="TorrentCreator.CreateFromUrlAsync"/>
    /// exactly, so the infohash computed at finalize is byte-identical to creating the torrent eagerly. Fire-and-
    /// forget from <c>WebTorrentClient.Add</c> (mirrors <c>InitFromMagnetAsync</c>); <c>AddAsync</c> awaits OnReady.
    /// </summary>
    public async Task InitLazyHashAsync(string url, WebTorrentClient client, AddTorrentOptions opts)
    {
        try
        {
            LazyUrl = url;
            // Stable provisional storage key (the infohash is unknown until finalize). The store + _state file key
            // on this, so a reload restores the cached pieces to the same dir — no re-download.
            _lazyPersistKey = LazyProvisionalKey(url);

            // Probe total size with a 0-0 range GET (servers that ignore the range still report Content-Length;
            // a 206 reports the total in Content-Range). No infohash needed — a web seed serves by URL + byte range.
            long length;
            using (var http = new HttpClient())
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Range = new RangeHeaderValue(0, 0);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                length = resp.Content.Headers.ContentRange?.Length
                         ?? resp.Content.Headers.ContentLength
                         ?? throw new InvalidOperationException($"Lazy-Hash: web seed reported no size for {url}");
            }
            if (length <= 0) throw new InvalidOperationException($"Lazy-Hash: web seed reported size {length} for {url}");

            // Name: SAME derivation as TorrentCreator.CreateFromUrlAsync (the name is in the info dict, so it must
            // match exactly or the finalized infohash won't). The web seed, however, must be the EXACT FILE URL:
            // WebConn (WebConn.cs:91-100) only appends the file name to a url-list entry that ends with '/'; the
            // exact URL is fetched by Range directly. (url-list is top-level, so this does NOT affect the infohash.)
            var uri = new Uri(url);
            var name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.TrimEnd('/') ?? "download.bin");

            int pieceLength = TorrentCreator.CalculatePieceLength(length);
            int pieceCount = (int)((length + pieceLength - 1) / pieceLength);
            const int hashLen = 32; // flat SHA-256 — TorrentCreator's default HashAlgorithm
            var pieceHashes = new byte[pieceCount][];
            for (int i = 0; i < pieceCount; i++) pieceHashes[i] = new byte[hashLen]; // zeroed placeholders, computed on download

            var meta = new TorrentMetadata
            {
                InfoHash = "",
                Name = name,
                PieceLength = pieceLength,
                PieceCount = pieceCount,
                TotalLength = length,
                PieceHashes = pieceHashes,
                Files = new[] { new TorrentFileInfo { Name = name, Path = name, Length = length, Offset = 0 } },
                UrlList = new[] { url },
                MetaVersion = 0,
            };

            LazyHash = true;
            InitFromMetadata(meta, client, opts); // sets up store, pieces/bitfield, attaches the web seed, fires OnReady
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Torrent] Lazy-Hash add failed for {url}: {ex.Message}");
            // Unblock AddAsync (it awaits OnReady) and leave this torrent in a non-functional state.
            Destroyed = true;
            OnReady?.Invoke();
        }
    }

    /// <summary>
    /// Called when a Lazy-Hash torrent finishes downloading (all pieces present + their hashes computed): assemble
    /// the real .torrent from the now-complete piece hashes using the SAME assembler as eager creation
    /// (<see cref="TorrentCreator.BuildTorrent"/>) → a byte-identical infohash. Adopt the infohash, capture the
    /// .torrent bytes, exit lazy mode, and notify. (Persistence re-keying + seeding land in later phases.)
    /// </summary>
    internal void FinalizeLazyHash()
    {
        if (!LazyHash) return;
        var name = Name;
        var files = Files;
        if (string.IsNullOrEmpty(name) || files == null || files.Length == 0) return; // shell always sets these
        try
        {
            var opts = new TorrentCreatorOptions { WebSeeds = UrlList ?? Array.Empty<string>() };
            var (torrentBytes, meta) = TorrentCreator.BuildTorrent(name, Length, PieceLength, _hashes.ToList(), opts, files);
            InfoHash = meta.InfoHash;
            TorrentFileBytes = torrentBytes;
            LazyHash = false;
            // Persist the now-complete .torrent under the SAME (provisional) PersistKey the pieces are stored under,
            // so a page reload restores the cached file with ZERO re-download. _lazyPersistKey is retained (PersistKey
            // stays the provisional id even though LazyHash is now false) so the store dir does not move.
            if (_client?.AsyncFileSystem != null) _ = PersistMetadataAsync();
            OnLazyFinalized?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Torrent] Lazy-Hash finalize failed: {ex.Message}");
        }
    }
}
