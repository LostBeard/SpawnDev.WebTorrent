using System.Security.Cryptography;
using System.Text;
using SpawnDev.WebTorrent.Bencode;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Creates .torrent files from local files, streams, URLs, or byte arrays.
/// Computes piece hashes, builds bencode structure, and produces
/// the raw .torrent bytes ready for distribution.
/// </summary>
public static class TorrentCreator
{
    /// <summary>
    /// Create a .torrent file from a Stream. Works on all platforms (desktop + browser).
    /// </summary>
    public static async Task<(byte[] torrentBytes, TorrentMetadata metadata)> CreateFromStreamAsync(
        string name, Stream stream, long length, TorrentCreatorOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TorrentCreatorOptions();
        if (options.MetaVersion == 2)
            throw new NotSupportedException("BEP 52 v2 torrent creation from a stream is not yet implemented. Buffer the stream and use CreateFromBytes with MetaVersion=2, or wait for the Phase 2a streaming follow-up.");

        int pieceLength = options.PieceLength > 0
            ? options.PieceLength
            : CalculatePieceLength(length);

        var pieceHashes = new List<byte[]>();
        var buffer = new byte[pieceLength];
        bool useSha256 = options.HashAlgorithm == "SHA-256";
        int bufferFill = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(bufferFill, pieceLength - bufferFill), ct)) > 0)
        {
            bufferFill += bytesRead;
            if (bufferFill == pieceLength)
            {
                pieceHashes.Add(useSha256
                    ? SHA256.HashData(buffer.AsSpan(0, bufferFill))
                    : SHA1.HashData(buffer.AsSpan(0, bufferFill)));
                bufferFill = 0;
            }
        }
        if (bufferFill > 0)
        {
            pieceHashes.Add(useSha256
                ? SHA256.HashData(buffer.AsSpan(0, bufferFill))
                : SHA1.HashData(buffer.AsSpan(0, bufferFill)));
        }

        name = options.Name ?? name;

        return BuildTorrent(name, length, pieceLength, pieceHashes, options,
            new[] { new TorrentFileInfo { Path = name, Name = name, Length = length, Offset = 0 } });
    }

    /// <summary>
    /// Create a .torrent file from a local file path. Desktop only — not available in browser.
    /// </summary>
    public static async Task<(byte[] torrentBytes, TorrentMetadata metadata)> CreateFromFileAsync(
        string filePath, TorrentCreatorOptions? options = null, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) throw new FileNotFoundException("File not found", filePath);
        using var fs = fileInfo.OpenRead();
        return await CreateFromStreamAsync(
            options?.Name ?? fileInfo.Name, fs, fileInfo.Length, options, ct);
    }

    /// <summary>
    /// Create a .torrent file from an HTTP/HTTPS URL. Downloads the file via streaming
    /// and computes piece hashes as data arrives — no full-file buffering.
    /// Works on all platforms (desktop + browser).
    /// </summary>
    public static async Task<(byte[] torrentBytes, TorrentMetadata metadata)> CreateFromUrlAsync(
        string url, TorrentCreatorOptions? options = null, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        using var response = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentLength
            ?? throw new InvalidOperationException($"Server did not provide Content-Length for {url}");

        var name = options?.Name;
        if (string.IsNullOrEmpty(name))
        {
            var uri = new Uri(url);
            name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.TrimEnd('/') ?? "download.bin");
        }

        // Add the original URL as a web seed
        options ??= new TorrentCreatorOptions();
        if (!options.WebSeeds.Contains(url))
        {
            var uri = new Uri(url);
            var basePath = string.Join("/", uri.Segments.Take(uri.Segments.Length - 1));
            var baseUrl = $"{uri.Scheme}://{uri.Authority}{basePath}".TrimEnd('/');
            if (!options.WebSeeds.Contains(baseUrl))
                options.WebSeeds = options.WebSeeds.Append(baseUrl).ToArray();
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await CreateFromStreamAsync(name, stream, length, options, ct);
    }

    /// <summary>
    /// Create a .torrent file from in-memory bytes. Respects <see cref="TorrentCreatorOptions.MetaVersion"/>:
    /// the default v1 path uses flat piece hashes; <c>MetaVersion = 2</c> builds a BEP 52
    /// v2 single-file torrent with Merkle-tree piece verification.
    /// </summary>
    public static (byte[] torrentBytes, TorrentMetadata metadata) CreateFromBytes(
        string name, byte[] data, TorrentCreatorOptions? options = null)
    {
        options ??= new TorrentCreatorOptions();
        int pieceLength = options.PieceLength > 0
            ? options.PieceLength
            : CalculatePieceLength(data.Length);

        if (options.MetaVersion == 2)
        {
            return BuildV2Torrent(name, data, pieceLength, options);
        }

        var pieceHashes = new List<byte[]>();
        bool useSha256 = options.HashAlgorithm == "SHA-256";
        for (int offset = 0; offset < data.Length; offset += pieceLength)
        {
            int len = Math.Min(pieceLength, data.Length - offset);
            pieceHashes.Add(useSha256
                ? SHA256.HashData(data.AsSpan(offset, len))
                : SHA1.HashData(data.AsSpan(offset, len)));
        }

        return BuildTorrent(name, data.Length, pieceLength, pieceHashes, options,
            new[] { new TorrentFileInfo { Path = name, Name = name, Length = data.Length, Offset = 0 } });
    }

    /// <summary>
    /// Create a multi-file .torrent from named byte arrays.
    /// Each entry is (relativePath, data). The torrent name is the root directory.
    /// Pieces are hashed across the concatenated file data (standard BitTorrent behavior).
    /// </summary>
    public static (byte[] torrentBytes, TorrentMetadata metadata) CreateFromMultipleFiles(
        string torrentName, (string path, byte[] data)[] files, TorrentCreatorOptions? options = null)
    {
        options ??= new TorrentCreatorOptions();
        if (options.MetaVersion == 2)
            throw new NotSupportedException("BEP 52 v2 multi-file torrent creation requires per-file piece alignment (Phase 2b). Only single-file v2 torrents via CreateFromBytes are supported in Phase 2a.");

        long totalLength = files.Sum(f => (long)f.data.Length);
        int pieceLength = options.PieceLength > 0
            ? options.PieceLength
            : CalculatePieceLength(totalLength);

        var pieceHashes = new List<byte[]>();
        bool useSha256 = options.HashAlgorithm == "SHA-256";

        // Hash pieces across concatenated file data
        var buffer = new byte[pieceLength];
        int bufferFill = 0;
        foreach (var file in files)
        {
            int fileOffset = 0;
            while (fileOffset < file.data.Length)
            {
                int toCopy = Math.Min(pieceLength - bufferFill, file.data.Length - fileOffset);
                Array.Copy(file.data, fileOffset, buffer, bufferFill, toCopy);
                bufferFill += toCopy;
                fileOffset += toCopy;

                if (bufferFill == pieceLength)
                {
                    pieceHashes.Add(useSha256
                        ? SHA256.HashData(buffer.AsSpan(0, bufferFill))
                        : SHA1.HashData(buffer.AsSpan(0, bufferFill)));
                    bufferFill = 0;
                }
            }
        }
        if (bufferFill > 0)
        {
            pieceHashes.Add(useSha256
                ? SHA256.HashData(buffer.AsSpan(0, bufferFill))
                : SHA1.HashData(buffer.AsSpan(0, bufferFill)));
        }

        // Build TorrentFileInfo entries with offsets
        var torrentFiles = new TorrentFileInfo[files.Length];
        long offset = 0;
        for (int i = 0; i < files.Length; i++)
        {
            var fileName = System.IO.Path.GetFileName(files[i].path);
            torrentFiles[i] = new TorrentFileInfo
            {
                Path = files[i].path,
                Name = fileName,
                Length = files[i].data.Length,
                Offset = offset,
            };
            offset += files[i].data.Length;
        }

        return BuildTorrent(torrentName, totalLength, pieceLength, pieceHashes, options, torrentFiles);
    }

    private static (byte[] torrentBytes, TorrentMetadata metadata) BuildTorrent(
        string name, long totalLength, int pieceLength, List<byte[]> pieceHashes,
        TorrentCreatorOptions options, TorrentFileInfo[] files)
    {
        // Concatenate piece hashes
        int hashSize = pieceHashes[0].Length;
        var piecesConcat = new byte[pieceHashes.Count * hashSize];
        for (int i = 0; i < pieceHashes.Count; i++)
            Array.Copy(pieceHashes[i], 0, piecesConcat, i * hashSize, hashSize);

        bool isMultiFile = files.Length > 1;

        // Build info dictionary (bencoded, raw bytes for hash computation)
        // Keys MUST be sorted alphabetically within the dict
        var infoParts = new List<byte>();
        infoParts.AddRange(Encoding.ASCII.GetBytes("d"));

        if (isMultiFile)
        {
            infoParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("files")));
            infoParts.AddRange(Encoding.ASCII.GetBytes("l"));
            foreach (var file in files)
            {
                infoParts.AddRange(Encoding.ASCII.GetBytes("d"));
                AppendBencodeKV(infoParts, "length", file.Length);
                infoParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("path")));
                infoParts.AddRange(Encoding.ASCII.GetBytes("l"));
                foreach (var part in file.Path.Split('/', '\\').Where(p => !string.IsNullOrEmpty(p)))
                    infoParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(part)));
                infoParts.AddRange(Encoding.ASCII.GetBytes("e"));
                infoParts.AddRange(Encoding.ASCII.GetBytes("e"));
            }
            infoParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }
        else
        {
            AppendBencodeKV(infoParts, "length", totalLength);
        }

        AppendBencodeKV(infoParts, "name", name);
        AppendBencodeKV(infoParts, "piece length", pieceLength);
        AppendBencodeKVBytes(infoParts, "pieces", piecesConcat);

        if (options.IsPrivate)
            AppendBencodeKV(infoParts, "private", 1L);

        infoParts.AddRange(Encoding.ASCII.GetBytes("e"));
        var infoBytes = infoParts.ToArray();
        var infoHashBytes = SHA1.HashData(infoBytes);
        var infoHashHex = Convert.ToHexString(infoHashBytes).ToLowerInvariant();

        // Build top-level dictionary
        var topParts = new List<byte>();
        topParts.AddRange(Encoding.ASCII.GetBytes("d"));

        if (options.Trackers.Length > 0)
            AppendBencodeKV(topParts, "announce", options.Trackers[0]);

        if (options.Trackers.Length > 1)
        {
            topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("announce-list")));
            topParts.AddRange(Encoding.ASCII.GetBytes("l"));
            foreach (var tracker in options.Trackers)
            {
                topParts.AddRange(Encoding.ASCII.GetBytes("l"));
                topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(tracker)));
                topParts.AddRange(Encoding.ASCII.GetBytes("e"));
            }
            topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }

        if (!string.IsNullOrEmpty(options.Comment))
            AppendBencodeKV(topParts, "comment", options.Comment);

        AppendBencodeKV(topParts, "created by", options.CreatedBy);
        AppendBencodeKV(topParts, "creation date", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("info")));
        topParts.AddRange(infoBytes);

        if (options.WebSeeds.Length > 0)
        {
            topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("url-list")));
            topParts.AddRange(Encoding.ASCII.GetBytes("l"));
            foreach (var ws in options.WebSeeds)
                topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(ws)));
            topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }

        topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        var torrentBytes = topParts.ToArray();

        var metadata = new TorrentMetadata
        {
            InfoHash = infoHashHex,
            InfoDictBytes = infoBytes,
            Name = name,
            TotalLength = totalLength,
            PieceLength = pieceLength,
            PieceCount = pieceHashes.Count,
            PieceHashes = pieceHashes.ToArray(),
            Files = files,
            AnnounceUrls = options.Trackers,
            UrlList = options.WebSeeds,
            CreatedBy = options.CreatedBy,
            CreationDate = DateTimeOffset.UtcNow,
            Comment = options.Comment,
            IsPrivate = options.IsPrivate,
            OriginalTorrentBytes = torrentBytes,
        };

        return (torrentBytes, metadata);
    }

    private static void AppendBencodeKV(List<byte> parts, string key, string value)
    {
        parts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(key)));
        parts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(value)));
    }

    private static void AppendBencodeKV(List<byte> parts, string key, long value)
    {
        parts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(key)));
        parts.AddRange(Encoding.ASCII.GetBytes($"i{value}e"));
    }

    private static void AppendBencodeKVBytes(List<byte> parts, string key, byte[] value)
    {
        parts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(key)));
        parts.AddRange(BencodeEncoder.EncodeBytes(value));
    }

    /// <summary>
    /// Build a BEP 52 v2 single-file torrent from in-memory bytes. The v2 info dict contains
    /// <c>file tree</c>, <c>meta version = 2</c>, <c>name</c>, and <c>piece length</c>. The
    /// file root (the "pieces root" for the single file) is computed via
    /// <see cref="MerkleHasher.ComputeFileRoot"/>. The <c>piece layers</c> dict sits at the
    /// top level of the torrent (outside the info dict) and is only populated when the file
    /// is strictly larger than the piece length.
    /// </summary>
    private static (byte[] torrentBytes, TorrentMetadata metadata) BuildV2Torrent(
        string name, byte[] data, int pieceLength, TorrentCreatorOptions options)
    {
        // BEP 52 piece length validation: must be a power-of-two multiple of 16 KiB.
        if (pieceLength < MerkleHasher.LeafSize || pieceLength % MerkleHasher.LeafSize != 0)
            throw new ArgumentException(
                $"BEP 52 v2 requires piece length to be a multiple of {MerkleHasher.LeafSize} (16 KiB). Got {pieceLength}.",
                nameof(options));
        int leavesPerPiece = pieceLength / MerkleHasher.LeafSize;
        if ((leavesPerPiece & (leavesPerPiece - 1)) != 0)
            throw new ArgumentException(
                $"BEP 52 v2 requires piece length / leaf size ({pieceLength}/{MerkleHasher.LeafSize}) to be a power of two.",
                nameof(options));

        // Compute Merkle tree for the single file.
        var fileRoot = MerkleHasher.ComputeFileRoot(data, pieceLength);

        // piece layers is only included for files strictly larger than the piece length.
        // Smaller-or-equal files carry all their hashing information in the single pieces root.
        byte[][] pieceLayerHashes = data.Length > pieceLength
            ? MerkleHasher.ComputePieceLayer(data, pieceLength)
            : Array.Empty<byte[]>();

        // Build the v2 info dict via the typed bencode encoder so nested dicts + alphabetical
        // key ordering are handled automatically. File tree uses UTF-8 filename keys which are
        // safe as strings; binary-keyed dicts (piece layers) are bencoded manually below.
        var fileTree = new Dictionary<string, object>
        {
            // BEP 52: file tree entry for a file uses an empty-string key to mark the leaf.
            [name] = new Dictionary<string, object>
            {
                [""] = new Dictionary<string, object>
                {
                    ["length"] = (long)data.Length,
                    ["pieces root"] = fileRoot,
                }
            }
        };

        var infoDict = new Dictionary<string, object>
        {
            ["file tree"] = fileTree,
            ["meta version"] = 2L,
            ["name"] = Encoding.UTF8.GetBytes(name),
            ["piece length"] = (long)pieceLength,
        };

        if (options.IsPrivate)
        {
            infoDict["private"] = 1L;
        }

        var infoBytes = BencodeEncoder.Encode(infoDict);
        var v2InfoHashBytes = SHA256.HashData(infoBytes);
        var v2InfoHashHex = Convert.ToHexString(v2InfoHashBytes).ToLowerInvariant();

        // Top-level torrent dict. Keys sorted alphabetically. We build this manually because the
        // "piece layers" dict has binary (SHA-256) keys that bencode as byte-string keys - the
        // typed encoder's string-keyed API can't express them safely.
        var topParts = new List<byte>();
        topParts.AddRange(Encoding.ASCII.GetBytes("d"));

        // announce
        if (options.Trackers.Length > 0)
            AppendBencodeKV(topParts, "announce", options.Trackers[0]);

        // announce-list
        if (options.Trackers.Length > 1)
        {
            topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("announce-list")));
            topParts.AddRange(Encoding.ASCII.GetBytes("l"));
            foreach (var tracker in options.Trackers)
            {
                topParts.AddRange(Encoding.ASCII.GetBytes("l"));
                topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(tracker)));
                topParts.AddRange(Encoding.ASCII.GetBytes("e"));
            }
            topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }

        if (!string.IsNullOrEmpty(options.Comment))
            AppendBencodeKV(topParts, "comment", options.Comment);

        AppendBencodeKV(topParts, "created by", options.CreatedBy);
        AppendBencodeKV(topParts, "creation date", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // info (raw pre-computed bytes)
        topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("info")));
        topParts.AddRange(infoBytes);

        // piece layers - only if multi-piece. Binary keys (SHA-256 roots).
        // Keys order: BEP 52 inherits bencode's sort-by-raw-byte-string rule. With a single
        // file we only have one key and order is trivial; multi-file hybrid (Phase 2b) will
        // need to sort the key bytes explicitly before emitting.
        if (pieceLayerHashes.Length > 0)
        {
            topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("piece layers")));
            topParts.AddRange(Encoding.ASCII.GetBytes("d"));

            // Key: the file root (32 bytes).
            topParts.AddRange(BencodeEncoder.EncodeBytes(fileRoot));

            // Value: concat of all piece-layer hashes.
            var concatenated = new byte[pieceLayerHashes.Length * MerkleHasher.HashSize];
            for (int i = 0; i < pieceLayerHashes.Length; i++)
                Buffer.BlockCopy(pieceLayerHashes[i], 0, concatenated, i * MerkleHasher.HashSize, MerkleHasher.HashSize);
            topParts.AddRange(BencodeEncoder.EncodeBytes(concatenated));

            topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }

        // url-list (web seeds)
        if (options.WebSeeds.Length > 0)
        {
            topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("url-list")));
            topParts.AddRange(Encoding.ASCII.GetBytes("l"));
            foreach (var ws in options.WebSeeds)
                topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(ws)));
            topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }

        topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        var torrentBytes = topParts.ToArray();

        // Concatenate piece-layer hashes for TorrentMetadata.PieceHashes (same storage shape
        // as v1, so downstream code that already branches on PieceHashAlgorithm sees consistent
        // data). For single-piece files the single piece's root IS the fileRoot.
        var pieceHashes = pieceLayerHashes.Length > 0
            ? pieceLayerHashes
            : new[] { fileRoot };

        var pieceLayersMap = new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);
        if (pieceLayerHashes.Length > 0)
        {
            var concatenated = new byte[pieceLayerHashes.Length * MerkleHasher.HashSize];
            for (int i = 0; i < pieceLayerHashes.Length; i++)
                Buffer.BlockCopy(pieceLayerHashes[i], 0, concatenated, i * MerkleHasher.HashSize, MerkleHasher.HashSize);
            pieceLayersMap[fileRoot] = concatenated;
        }

        var metadata = new TorrentMetadata
        {
            // v2-only torrents have no v1 info hash; leave InfoHash empty.
            InfoHash = "",
            V2InfoHash = v2InfoHashHex,
            MetaVersion = 2,
            InfoDictBytes = infoBytes,
            Name = name,
            TotalLength = data.Length,
            PieceLength = pieceLength,
            PieceCount = pieceHashes.Length,
            PieceHashes = pieceHashes,
            FileRoots = new[] { fileRoot },
            PieceLayers = pieceLayersMap,
            Files = new[] { new TorrentFileInfo { Path = name, Name = name, Length = data.Length, Offset = 0 } },
            AnnounceUrls = options.Trackers,
            UrlList = options.WebSeeds,
            CreatedBy = options.CreatedBy,
            CreationDate = DateTimeOffset.UtcNow,
            Comment = options.Comment,
            IsPrivate = options.IsPrivate,
            OriginalTorrentBytes = torrentBytes,
        };

        return (torrentBytes, metadata);
    }

    private static int CalculatePieceLength(long fileSize)
    {
        if (fileSize < 16 * 1024 * 1024) return 16 * 1024;
        if (fileSize < 128 * 1024 * 1024) return 64 * 1024;
        if (fileSize < 512 * 1024 * 1024) return 256 * 1024;
        if (fileSize < 2L * 1024 * 1024 * 1024) return 1024 * 1024;
        return 4 * 1024 * 1024;
    }
}

/// <summary>Torrent creation options.</summary>
public class TorrentCreatorOptions
{
    /// <summary>Override torrent name (default: filename).</summary>
    public string? Name { get; set; }

    /// <summary>Piece size in bytes (0 = auto-calculate).</summary>
    public int PieceLength { get; set; }

    /// <summary>Tracker announce URLs.</summary>
    public string[] Trackers { get; set; } = Array.Empty<string>();

    /// <summary>Web seed URLs.</summary>
    public string[] WebSeeds { get; set; } = Array.Empty<string>();

    /// <summary>Comment to embed in .torrent file.</summary>
    public string? Comment { get; set; }

    /// <summary>Creator identification string.</summary>
    public string CreatedBy { get; set; } = "SpawnDev.WebTorrent";

    /// <summary>Private torrent (no DHT/PEX).</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Hash algorithm for piece verification. "SHA-256" (default) or "SHA-1".</summary>
    public string HashAlgorithm { get; set; } = "SHA-256";

    /// <summary>
    /// BEP 52 meta version. <c>0</c> or <c>1</c> (default) produces a classic v1 torrent
    /// with a flat piece hash list (SHA-1 or SHA-256 per <see cref="HashAlgorithm"/>).
    /// <c>2</c> produces a BEP 52 v2-only torrent with a Merkle-tree structure, a
    /// <c>file tree</c> info dict, per-file <c>pieces root</c> values, a top-level
    /// <c>piece layers</c> dict for multi-piece files, and a SHA-256 info hash. v2
    /// always uses SHA-256 regardless of the <see cref="HashAlgorithm"/> field. Only
    /// <see cref="TorrentCreator.CreateFromBytes"/> supports v2 today; streaming and
    /// multi-file entry points throw <see cref="NotSupportedException"/> until the
    /// Phase 2a streaming follow-up and Phase 2b per-file alignment land.
    /// </summary>
    public int MetaVersion { get; set; } = 1;
}
