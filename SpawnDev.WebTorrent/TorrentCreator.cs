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

        int pieceLength = options.PieceLength > 0
            ? options.PieceLength
            : CalculatePieceLength(length);

        if (options.MetaVersion == 2)
        {
            return options.Hybrid
                ? await CreateHybridSingleFileFromStreamAsync(name, stream, pieceLength, options, ct)
                : await CreateV2FromStreamAsync(name, stream, length, pieceLength, options, ct);
        }

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
            return options.Hybrid
                ? BuildHybridSingleFile(name, data, pieceLength, options)
                : BuildV2Torrent(name, data, pieceLength, options);
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
        {
            return options.Hybrid
                ? BuildHybridMultiFile(torrentName, files, options)
                : BuildV2MultiFile(torrentName, files, options);
        }

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
    /// Build a BEP 52 v2 single-file torrent from in-memory bytes. Computes the Merkle tree
    /// via <see cref="MerkleHasher"/> and delegates assembly to <see cref="AssembleV2Torrent"/>.
    /// </summary>
    private static (byte[] torrentBytes, TorrentMetadata metadata) BuildV2Torrent(
        string name, byte[] data, int pieceLength, TorrentCreatorOptions options)
    {
        ValidateV2PieceSize(pieceLength);

        var fileRoot = MerkleHasher.ComputeFileRoot(data, pieceLength);
        byte[][] pieceLayerHashes = data.Length > pieceLength
            ? MerkleHasher.ComputePieceLayer(data, pieceLength)
            : Array.Empty<byte[]>();

        return AssembleV2Torrent(name, data.Length, pieceLength, fileRoot, pieceLayerHashes, options);
    }

    private static void ValidateV2PieceSize(int pieceLength)
    {
        // BEP 52 piece length validation: must be a power-of-two multiple of 16 KiB.
        if (pieceLength < MerkleHasher.LeafSize || pieceLength % MerkleHasher.LeafSize != 0)
            throw new ArgumentException(
                $"BEP 52 v2 requires piece length to be a multiple of {MerkleHasher.LeafSize} (16 KiB). Got {pieceLength}.",
                nameof(pieceLength));
        int leavesPerPiece = pieceLength / MerkleHasher.LeafSize;
        if ((leavesPerPiece & (leavesPerPiece - 1)) != 0)
            throw new ArgumentException(
                $"BEP 52 v2 requires piece length / leaf size ({pieceLength}/{MerkleHasher.LeafSize}) to be a power of two.",
                nameof(pieceLength));
    }

    /// <summary>
    /// Streaming v2 torrent creation. Feeds the input stream through an incremental Merkle
    /// hasher so large files (model weights, datasets) can be hashed without full buffering.
    /// Memory footprint is bounded at roughly <c>pieceLength / 16 KiB</c> leaf hashes per
    /// in-progress piece plus 32 bytes per completed piece root.
    /// </summary>
    private static async Task<(byte[] torrentBytes, TorrentMetadata metadata)> CreateV2FromStreamAsync(
        string name, Stream stream, long length, int pieceLength, TorrentCreatorOptions options, CancellationToken ct)
    {
        ValidateV2PieceSize(pieceLength);

        var hasher = MerkleHasher.CreateIncremental(pieceLength);
        // Read in comfortable-sized chunks. The hasher absorbs any size, so we use the piece
        // length as a natural unit - it lines up with the encoder's internal accumulation.
        int bufferSize = Math.Max(pieceLength, 64 * 1024);
        var buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
        {
            hasher.Update(buffer.AsSpan(0, bytesRead));
        }
        var (fileRoot, pieceLayerHashes) = hasher.Finish();

        // Trust the hasher's byte count over the declared length; the stream may have been
        // shorter than advertised. The spec-shaped fields reflect what was actually hashed.
        long actualLength = hasher.TotalBytesHashed;

        return AssembleV2Torrent(name, actualLength, pieceLength, fileRoot, pieceLayerHashes, options);
    }

    /// <summary>
    /// Pure assembly of a BEP 52 v2 single-file torrent from pre-computed Merkle results.
    /// Shared between the in-memory (<see cref="BuildV2Torrent"/>) and streaming
    /// (<see cref="CreateV2FromStreamAsync"/>) paths so both produce bit-identical output
    /// for the same input bytes.
    /// </summary>
    private static (byte[] torrentBytes, TorrentMetadata metadata) AssembleV2Torrent(
        string name, long length, int pieceLength, byte[] fileRoot, byte[][] pieceLayerHashes, TorrentCreatorOptions options)
    {
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
                    ["length"] = length,
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

        // Build the sorted piece layers list (for single file, 0 or 1 entry).
        var sortedLayers = new List<(byte[] key, byte[] value)>();
        if (pieceLayerHashes.Length > 0)
        {
            var concatenated = ConcatPieceLayerHashes(pieceLayerHashes);
            sortedLayers.Add((fileRoot, concatenated));
        }

        var torrentBytes = BuildV2TopLevelBytes(infoBytes, sortedLayers, options);

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
            TotalLength = length,
            PieceLength = pieceLength,
            PieceCount = pieceHashes.Length,
            PieceHashes = pieceHashes,
            FileRoots = new[] { fileRoot },
            PieceLayers = pieceLayersMap,
            Files = new[] { new TorrentFileInfo { Path = name, Name = name, Length = length, Offset = 0 } },
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

    /// <summary>
    /// Concatenates piece-layer hashes into a single byte array for storage inside the
    /// piece layers dict value. One 32-byte entry per piece, in order.
    /// </summary>
    private static byte[] ConcatPieceLayerHashes(byte[][] pieceLayerHashes)
    {
        var concatenated = new byte[pieceLayerHashes.Length * MerkleHasher.HashSize];
        for (int i = 0; i < pieceLayerHashes.Length; i++)
            Buffer.BlockCopy(pieceLayerHashes[i], 0, concatenated, i * MerkleHasher.HashSize, MerkleHasher.HashSize);
        return concatenated;
    }

    /// <summary>
    /// Assembles the top-level bytes of a BEP 52 v2 torrent from pre-computed info-dict bytes
    /// and a piece layers map. The caller is responsible for sorting the layers by key-byte
    /// order (BEP 52 requires dict keys be sorted as raw byte strings). Shared between the
    /// single-file and multi-file v2 creators so both emit identically shaped output.
    /// </summary>
    private static byte[] BuildV2TopLevelBytes(
        byte[] infoBytes,
        IReadOnlyList<(byte[] key, byte[] value)> sortedPieceLayers,
        TorrentCreatorOptions options)
    {
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

        if (sortedPieceLayers.Count > 0)
        {
            topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("piece layers")));
            topParts.AddRange(Encoding.ASCII.GetBytes("d"));
            foreach (var (key, value) in sortedPieceLayers)
            {
                topParts.AddRange(BencodeEncoder.EncodeBytes(key));
                topParts.AddRange(BencodeEncoder.EncodeBytes(value));
            }
            topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }

        if (options.WebSeeds.Length > 0)
        {
            topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("url-list")));
            topParts.AddRange(Encoding.ASCII.GetBytes("l"));
            foreach (var ws in options.WebSeeds)
                topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes(ws)));
            topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        }

        topParts.AddRange(Encoding.ASCII.GetBytes("e"));
        return topParts.ToArray();
    }

    /// <summary>
    /// Sort piece-layer entries by key bytes per BEP 52 (dict keys must be sorted as raw
    /// byte strings on the wire). Stable across calls for a given input.
    /// </summary>
    private static List<(byte[] key, byte[] value)> SortPieceLayers(IEnumerable<(byte[] key, byte[] value)> entries)
    {
        var list = entries.ToList();
        list.Sort((a, b) => CompareBytes(a.key, b.key));
        return list;
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return a.Length - b.Length;
    }

    /// <summary>
    /// Build a BEP 52 hybrid v1+v2 single-file torrent. The single info dict carries both
    /// the v1 keys (length, name, piece length, pieces as flat SHA-1 hashes) and the v2 keys
    /// (file tree, meta version = 2, pieces root via Merkle tree). Two valid infohashes -
    /// SHA-1 of the info dict for v1 clients, SHA-256 of the same bytes for v2 clients.
    ///
    /// Single-file hybrid needs no padding because there is only one file in the piece stream;
    /// its last piece is the partial piece of the file itself, which v1 clients already
    /// handle correctly.
    /// </summary>
    private static (byte[] torrentBytes, TorrentMetadata metadata) BuildHybridSingleFile(
        string name, byte[] data, int pieceLength, TorrentCreatorOptions options)
    {
        ValidateV2PieceSize(pieceLength);

        // v1 piece hashes: flat SHA-1 over piece-sized chunks of the file.
        var v1PieceHashes = new List<byte[]>();
        for (int offset = 0; offset < data.Length; offset += pieceLength)
        {
            int len = Math.Min(pieceLength, data.Length - offset);
            v1PieceHashes.Add(SHA1.HashData(data.AsSpan(offset, len)));
        }

        // v2 Merkle tree over the file.
        var fileRoot = MerkleHasher.ComputeFileRoot(data, pieceLength);
        byte[][] pieceLayerHashes = data.Length > pieceLength
            ? MerkleHasher.ComputePieceLayer(data, pieceLength)
            : Array.Empty<byte[]>();

        return AssembleHybridSingleFile(name, data.Length, pieceLength,
            v1PieceHashes.ToArray(), fileRoot, pieceLayerHashes, options);
    }

    /// <summary>
    /// Streaming hybrid single-file torrent creation. Reads the input stream in piece-sized
    /// chunks, hashing each full piece with both SHA-1 (for v1 pieces) and feeding it into
    /// an incremental Merkle hasher (for v2 file tree + piece layers). Single pass, bounded
    /// memory - one piece buffer + incremental Merkle state. Suitable for multi-GiB files
    /// that cannot fit in memory.
    /// </summary>
    private static async Task<(byte[] torrentBytes, TorrentMetadata metadata)> CreateHybridSingleFileFromStreamAsync(
        string name, Stream stream, int pieceLength, TorrentCreatorOptions options, CancellationToken ct)
    {
        ValidateV2PieceSize(pieceLength);

        var v1PieceHashes = new List<byte[]>();
        var merkle = MerkleHasher.CreateIncremental(pieceLength);
        var pieceBuffer = new byte[pieceLength];
        int pieceFill = 0;
        long totalBytes = 0;

        var readBuf = new byte[Math.Max(pieceLength, 64 * 1024)];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), ct)) > 0)
        {
            int srcPos = 0;
            while (srcPos < bytesRead)
            {
                int toCopy = Math.Min(pieceLength - pieceFill, bytesRead - srcPos);
                Buffer.BlockCopy(readBuf, srcPos, pieceBuffer, pieceFill, toCopy);
                pieceFill += toCopy;
                srcPos += toCopy;
                totalBytes += toCopy;

                if (pieceFill == pieceLength)
                {
                    v1PieceHashes.Add(SHA1.HashData(pieceBuffer.AsSpan(0, pieceLength)));
                    merkle.Update(pieceBuffer.AsSpan(0, pieceLength));
                    pieceFill = 0;
                }
            }
        }
        if (pieceFill > 0)
        {
            // Last partial piece: v1 hashes the partial content directly (no padding - v1
            // allows the last piece to be short). Merkle absorbs the same bytes; its
            // per-leaf zero-padding is internal to the v2 tree, not visible as v1 pieces.
            v1PieceHashes.Add(SHA1.HashData(pieceBuffer.AsSpan(0, pieceFill)));
            merkle.Update(pieceBuffer.AsSpan(0, pieceFill));
        }
        var (fileRoot, pieceLayerHashes) = merkle.Finish();

        return AssembleHybridSingleFile(name, totalBytes, pieceLength,
            v1PieceHashes.ToArray(), fileRoot, pieceLayerHashes, options);
    }

    /// <summary>
    /// Pure assembly of a BEP 52 hybrid v1+v2 single-file torrent from pre-computed hashing
    /// results. Shared between in-memory <see cref="BuildHybridSingleFile"/> and streaming
    /// <see cref="CreateHybridSingleFileFromStreamAsync"/> so both produce bit-identical
    /// output for the same input bytes.
    /// </summary>
    private static (byte[] torrentBytes, TorrentMetadata metadata) AssembleHybridSingleFile(
        string name, long length, int pieceLength,
        byte[][] v1PieceHashes, byte[] fileRoot, byte[][] pieceLayerHashes,
        TorrentCreatorOptions options)
    {
        // v1 pieces concat (20 bytes per SHA-1 hash).
        var v1PiecesConcat = new byte[v1PieceHashes.Length * 20];
        for (int i = 0; i < v1PieceHashes.Length; i++)
            Buffer.BlockCopy(v1PieceHashes[i], 0, v1PiecesConcat, i * 20, 20);

        // Hybrid info dict: union of v1 and v2 key sets, alphabetically sorted.
        var fileTree = new Dictionary<string, object>
        {
            [name] = new Dictionary<string, object>
            {
                [""] = new Dictionary<string, object>
                {
                    ["length"] = length,
                    ["pieces root"] = fileRoot,
                }
            }
        };
        var infoDict = new Dictionary<string, object>
        {
            ["file tree"] = fileTree,
            ["length"] = length,
            ["meta version"] = 2L,
            ["name"] = Encoding.UTF8.GetBytes(name),
            ["piece length"] = (long)pieceLength,
            ["pieces"] = v1PiecesConcat,
        };
        if (options.IsPrivate) infoDict["private"] = 1L;

        var infoBytes = BencodeEncoder.Encode(infoDict);
        var v1InfoHashHex = Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();
        var v2InfoHashHex = Convert.ToHexString(SHA256.HashData(infoBytes)).ToLowerInvariant();

        var sortedLayers = new List<(byte[] key, byte[] value)>();
        var pieceLayersMap = new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);
        if (pieceLayerHashes.Length > 0)
        {
            var concat = ConcatPieceLayerHashes(pieceLayerHashes);
            sortedLayers.Add((fileRoot, concat));
            pieceLayersMap[fileRoot] = concat;
        }
        var torrentBytes = BuildV2TopLevelBytes(infoBytes, sortedLayers, options);

        var pieceHashes = pieceLayerHashes.Length > 0
            ? pieceLayerHashes
            : new[] { fileRoot };

        var metadata = new TorrentMetadata
        {
            InfoHash = v1InfoHashHex,
            V2InfoHash = v2InfoHashHex,
            MetaVersion = 2,
            InfoDictBytes = infoBytes,
            Name = name,
            TotalLength = length,
            PieceLength = pieceLength,
            PieceCount = pieceHashes.Length,
            PieceHashes = pieceHashes,
            FileRoots = new[] { fileRoot },
            PieceLayers = pieceLayersMap,
            Files = new[] { new TorrentFileInfo { Path = name, Name = name, Length = length, Offset = 0 } },
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

    /// <summary>
    /// Build a BEP 52 hybrid v1+v2 multi-file torrent. Each real file is followed in the v1
    /// files list by a pad-file entry (attr = "p", path = [".pad", "&lt;padLen&gt;"])
    /// sized to fill out to the next piece boundary - except the last file, which may end
    /// partially mid-piece as usual for v1. The single info dict carries both the v1 flat
    /// piece hashes (SHA-1 over piece-aligned chunks of the padded virtual stream) and the
    /// v2 Merkle tree (per-file roots over the REAL bytes, no padding).
    /// </summary>
    private static (byte[] torrentBytes, TorrentMetadata metadata) BuildHybridMultiFile(
        string torrentName, (string path, byte[] data)[] files, TorrentCreatorOptions options)
    {
        if (files.Length == 0)
            throw new ArgumentException("Hybrid multi-file torrent requires at least one file.", nameof(files));

        long realTotalLength = files.Sum(f => (long)f.data.Length);
        int pieceLength = options.PieceLength > 0
            ? options.PieceLength
            : CalculatePieceLength(realTotalLength);
        ValidateV2PieceSize(pieceLength);

        // Build v1 files list with pad entries between real files.
        var v1FilesList = new List<object>();
        long paddedOffset = 0;
        var realFileOffsets = new long[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            realFileOffsets[i] = paddedOffset;
            v1FilesList.Add(new Dictionary<string, object>
            {
                ["length"] = (long)files[i].data.Length,
                ["path"] = SplitPathForBencode(files[i].path),
            });
            paddedOffset += files[i].data.Length;

            bool isLast = i == files.Length - 1;
            if (!isLast && files[i].data.Length % pieceLength != 0)
            {
                long padLen = pieceLength - (files[i].data.Length % pieceLength);
                v1FilesList.Add(new Dictionary<string, object>
                {
                    ["attr"] = Encoding.UTF8.GetBytes("p"),
                    ["length"] = padLen,
                    ["path"] = new List<object>
                    {
                        Encoding.UTF8.GetBytes(".pad"),
                        Encoding.UTF8.GetBytes(padLen.ToString()),
                    },
                });
                paddedOffset += padLen;
            }
        }

        // Compute v1 pieces over the padded virtual stream. Because each real file starts
        // at a piece boundary and pad bytes are zeros, each piece contains content from
        // EXACTLY ONE real file (possibly with zero-pad tail on the last piece of that file).
        var v1PieceHashes = new List<byte[]>();
        for (int i = 0; i < files.Length; i++)
        {
            var data = files[i].data;
            bool isLast = i == files.Length - 1;
            for (int offset = 0; offset < data.Length; offset += pieceLength)
            {
                int actualLen = Math.Min(pieceLength, data.Length - offset);
                if (actualLen == pieceLength)
                {
                    v1PieceHashes.Add(SHA1.HashData(data.AsSpan(offset, pieceLength)));
                }
                else
                {
                    // Partial piece - last piece of this file.
                    // If not the last file, pad file fills it to piece boundary. If this IS
                    // the last file, last piece is genuinely partial (v1 behavior).
                    if (!isLast)
                    {
                        var padded = new byte[pieceLength];
                        data.AsSpan(offset, actualLen).CopyTo(padded);
                        v1PieceHashes.Add(SHA1.HashData(padded));
                    }
                    else
                    {
                        v1PieceHashes.Add(SHA1.HashData(data.AsSpan(offset, actualLen)));
                    }
                }
            }
        }

        var v1PiecesConcat = new byte[v1PieceHashes.Count * 20];
        for (int i = 0; i < v1PieceHashes.Count; i++)
            Buffer.BlockCopy(v1PieceHashes[i], 0, v1PiecesConcat, i * 20, 20);

        // v2 per-file Merkle (over REAL bytes, no padding).
        var fileRoots = new byte[files.Length][];
        var filePieceLayers = new byte[files.Length][][];
        for (int i = 0; i < files.Length; i++)
        {
            fileRoots[i] = MerkleHasher.ComputeFileRoot(files[i].data, pieceLength);
            filePieceLayers[i] = files[i].data.Length > pieceLength
                ? MerkleHasher.ComputePieceLayer(files[i].data, pieceLength)
                : Array.Empty<byte[]>();
        }
        var v2FileTree = BuildV2FileTree(files, fileRoots);

        // Combined hybrid info dict: v1 keys (files, name, piece length, pieces) + v2 keys
        // (file tree, meta version, name, piece length). 'name' and 'piece length' are
        // shared; BEP 52 requires they appear once.
        var infoDict = new Dictionary<string, object>
        {
            ["file tree"] = v2FileTree,
            ["files"] = v1FilesList,
            ["meta version"] = 2L,
            ["name"] = Encoding.UTF8.GetBytes(torrentName),
            ["piece length"] = (long)pieceLength,
            ["pieces"] = v1PiecesConcat,
        };
        if (options.IsPrivate) infoDict["private"] = 1L;

        var infoBytes = BencodeEncoder.Encode(infoDict);
        var v1InfoHashBytes = SHA1.HashData(infoBytes);
        var v1InfoHashHex = Convert.ToHexString(v1InfoHashBytes).ToLowerInvariant();
        var v2InfoHashBytes = SHA256.HashData(infoBytes);
        var v2InfoHashHex = Convert.ToHexString(v2InfoHashBytes).ToLowerInvariant();

        // Sorted piece layers.
        var layersCollector = new List<(byte[] key, byte[] value)>();
        var pieceLayersMap = new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);
        for (int i = 0; i < files.Length; i++)
        {
            if (filePieceLayers[i].Length > 0)
            {
                var concat = ConcatPieceLayerHashes(filePieceLayers[i]);
                layersCollector.Add((fileRoots[i], concat));
                pieceLayersMap[fileRoots[i]] = concat;
            }
        }
        var sortedLayers = SortPieceLayers(layersCollector);

        var torrentBytes = BuildV2TopLevelBytes(infoBytes, sortedLayers, options);

        // TorrentMetadata Files[] holds the REAL files (pad files are internal v1 bookkeeping).
        // Offsets reflect the padded virtual stream layout so piece-verification math works.
        var torrentFiles = new TorrentFileInfo[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            torrentFiles[i] = new TorrentFileInfo
            {
                Path = files[i].path,
                Name = System.IO.Path.GetFileName(files[i].path),
                Length = files[i].data.Length,
                Offset = realFileOffsets[i],
            };
        }

        // PieceHashes: prefer v2 layer for consistency across v2-aware paths. Consumers that
        // want the v1 pieces stream can re-decode from InfoDictBytes.
        var flatHashes = new List<byte[]>();
        for (int i = 0; i < files.Length; i++)
        {
            if (filePieceLayers[i].Length > 0)
                flatHashes.AddRange(filePieceLayers[i]);
            else if (files[i].data.Length > 0)
                flatHashes.Add(fileRoots[i]);
        }

        var metadata = new TorrentMetadata
        {
            InfoHash = v1InfoHashHex,
            V2InfoHash = v2InfoHashHex,
            MetaVersion = 2,
            InfoDictBytes = infoBytes,
            Name = torrentName,
            TotalLength = realTotalLength,
            PieceLength = pieceLength,
            PieceCount = flatHashes.Count,
            PieceHashes = flatHashes.ToArray(),
            FileRoots = fileRoots,
            PieceLayers = pieceLayersMap,
            Files = torrentFiles,
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

    /// <summary>
    /// Splits a file path on / and \\ into a bencode-ready path component list, suitable for
    /// assigning as the "path" value in a v1 files entry.
    /// </summary>
    private static List<object> SplitPathForBencode(string path)
    {
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<object>(parts.Length);
        foreach (var p in parts)
            list.Add(Encoding.UTF8.GetBytes(p));
        return list;
    }

    /// <summary>
    /// Build a BEP 52 v2 multi-file torrent from in-memory file data. Produces a recursive
    /// file tree reflecting the input paths' directory structure, one Merkle tree per file,
    /// and a piece layers dict keyed on per-file root hashes (sorted per BEP 52).
    ///
    /// This method does NOT perform per-file piece alignment - pieces are computed
    /// independently per file, so the v2 torrent is NOT safe to mix with a v1 interpretation
    /// of the same content (pieces would not line up). Pure-v2 consumers are fine; hybrid
    /// v1+v2 torrents are a Phase 2b feature requiring explicit alignment padding.
    /// </summary>
    private static (byte[] torrentBytes, TorrentMetadata metadata) BuildV2MultiFile(
        string torrentName, (string path, byte[] data)[] files, TorrentCreatorOptions options)
    {
        if (files.Length == 0)
            throw new ArgumentException("Multi-file torrent requires at least one file.", nameof(files));

        long totalLength = files.Sum(f => (long)f.data.Length);
        int pieceLength = options.PieceLength > 0
            ? options.PieceLength
            : CalculatePieceLength(totalLength);
        ValidateV2PieceSize(pieceLength);

        // Sort input files into BEP 52 file-tree walk order (bytewise path ordering) so every
        // downstream per-file structure - fileRoots[], filePieceLayers[], flatHashes[],
        // torrentFiles[] - is in the SAME order a parse round-trip sees. Prevents a subtle
        // bug where a creator built from input order ["b", "a"] produces a TorrentMetadata
        // with PieceHashes in input order while TorrentParser.Parse(bytes) of the same torrent
        // reads them back in alphabetical order, making globalPieceIndex ambiguous between
        // the two sides.
        files = files.OrderBy(f => f.path, StringComparer.Ordinal).ToArray();

        // Hash each file independently, collecting file roots and piece layers.
        var fileRoots = new byte[files.Length][];
        var filePieceLayers = new byte[files.Length][][];
        for (int i = 0; i < files.Length; i++)
        {
            fileRoots[i] = MerkleHasher.ComputeFileRoot(files[i].data, pieceLength);
            filePieceLayers[i] = files[i].data.Length > pieceLength
                ? MerkleHasher.ComputePieceLayer(files[i].data, pieceLength)
                : Array.Empty<byte[]>();
        }

        // Build the nested file tree from the path list.
        var fileTree = BuildV2FileTree(files, fileRoots);

        var infoDict = new Dictionary<string, object>
        {
            ["file tree"] = fileTree,
            ["meta version"] = 2L,
            ["name"] = Encoding.UTF8.GetBytes(torrentName),
            ["piece length"] = (long)pieceLength,
        };
        if (options.IsPrivate) infoDict["private"] = 1L;

        var infoBytes = BencodeEncoder.Encode(infoDict);
        var v2InfoHashBytes = SHA256.HashData(infoBytes);
        var v2InfoHashHex = Convert.ToHexString(v2InfoHashBytes).ToLowerInvariant();

        // Build sorted piece layers for files > pieceLength.
        var layersCollector = new List<(byte[] key, byte[] value)>();
        var pieceLayersMap = new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);
        for (int i = 0; i < files.Length; i++)
        {
            if (filePieceLayers[i].Length > 0)
            {
                var concat = ConcatPieceLayerHashes(filePieceLayers[i]);
                layersCollector.Add((fileRoots[i], concat));
                pieceLayersMap[fileRoots[i]] = concat;
            }
        }
        var sortedLayers = SortPieceLayers(layersCollector);

        var torrentBytes = BuildV2TopLevelBytes(infoBytes, sortedLayers, options);

        // Build TorrentFileInfo array with PADDED offsets. BEP 52 §"File tree" defines the
        // piece index as the logical concatenation of files with implicit zero-padding so
        // each file starts on a piece boundary in the virtual stream. Offsets reflect the
        // padded virtual layout (consumer-facing math for piece-to-file mapping), NOT the
        // raw per-file byte counts.
        var torrentFiles = new TorrentFileInfo[files.Length];
        long offset = 0;
        for (int i = 0; i < files.Length; i++)
        {
            torrentFiles[i] = new TorrentFileInfo
            {
                Path = files[i].path,
                Name = System.IO.Path.GetFileName(files[i].path),
                Length = files[i].data.Length,
                Offset = offset,
            };
            offset += files[i].data.Length;
            // Pad each file's tail up to the next piece boundary (implicit - no actual bytes
            // are emitted; this only shifts the virtual offset of the next file).
            if (files[i].data.Length > 0)
            {
                long rem = files[i].data.Length % pieceLength;
                if (rem != 0) offset += (pieceLength - rem);
            }
        }

        // Flatten piece hashes across all files for PieceHashes. Order matches file-tree walk
        // (input already sorted above) so a parse round-trip produces the same sequence.
        // Single-piece files (file.Length <= PieceLength) contribute their file root as the
        // single piece hash.
        var flatHashes = new List<byte[]>();
        for (int i = 0; i < files.Length; i++)
        {
            if (filePieceLayers[i].Length > 0)
                flatHashes.AddRange(filePieceLayers[i]);
            else if (files[i].data.Length > 0)
                flatHashes.Add(fileRoots[i]);
        }

        var metadata = new TorrentMetadata
        {
            InfoHash = "",
            V2InfoHash = v2InfoHashHex,
            MetaVersion = 2,
            InfoDictBytes = infoBytes,
            Name = torrentName,
            TotalLength = totalLength,
            PieceLength = pieceLength,
            PieceCount = flatHashes.Count,
            PieceHashes = flatHashes.ToArray(),
            FileRoots = fileRoots,
            PieceLayers = pieceLayersMap,
            Files = torrentFiles,
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

    /// <summary>
    /// Constructs a BEP 52 file-tree dictionary from a list of files with relative paths.
    /// Paths are split on / and \\ into directory components. Duplicate paths throw. Empty
    /// path components (e.g. "a//b" or leading slash) are rejected.
    /// </summary>
    private static Dictionary<string, object> BuildV2FileTree(
        (string path, byte[] data)[] files, byte[][] fileRoots)
    {
        var root = new Dictionary<string, object>();

        for (int i = 0; i < files.Length; i++)
        {
            var path = files[i].path;
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"File path at index {i} is empty or whitespace.", nameof(files));

            var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.None);
            if (parts.Any(string.IsNullOrEmpty))
                throw new ArgumentException($"File path '{path}' has empty path components (leading/trailing slash or double slash).", nameof(files));

            var current = root;
            for (int j = 0; j < parts.Length - 1; j++)
            {
                if (current.TryGetValue(parts[j], out var existing))
                {
                    // An existing Dictionary<string, object> with an empty-string key is a file
                    // leaf, not a directory - the incoming path would treat a file as a parent.
                    if (existing is not Dictionary<string, object> existingDict || existingDict.ContainsKey(""))
                        throw new ArgumentException($"Path '{path}' conflicts with existing file at component '{parts[j]}'.", nameof(files));
                    current = existingDict;
                }
                else
                {
                    var subDict = new Dictionary<string, object>();
                    current[parts[j]] = subDict;
                    current = subDict;
                }
            }

            var leafName = parts[^1];
            if (current.ContainsKey(leafName))
                throw new ArgumentException($"Duplicate file path '{path}' (or path component collides with existing directory).", nameof(files));

            current[leafName] = new Dictionary<string, object>
            {
                [""] = new Dictionary<string, object>
                {
                    ["length"] = (long)files[i].data.Length,
                    ["pieces root"] = fileRoots[i],
                }
            };
        }

        return root;
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
    /// <c>2</c> produces a BEP 52 v2 torrent with a Merkle-tree structure, a
    /// <c>file tree</c> info dict, per-file <c>pieces root</c> values, a top-level
    /// <c>piece layers</c> dict for multi-piece files, and a SHA-256 info hash. v2
    /// always uses SHA-256 regardless of the <see cref="HashAlgorithm"/> field.
    /// Combine with <see cref="Hybrid"/> = <c>true</c> for a hybrid v1+v2 torrent.
    /// </summary>
    public int MetaVersion { get; set; } = 1;

    /// <summary>
    /// When <c>true</c> and <see cref="MetaVersion"/> = 2, produce a hybrid v1+v2 torrent:
    /// a single info dict carrying both the v1 flat piece hashes and the v2 Merkle tree,
    /// yielding two valid infohashes (SHA-1 over the info dict for v1 clients, SHA-256 for
    /// v2 clients). Multi-file hybrid torrents pad each file to a piece boundary with
    /// pad-file entries (<c>attr = "p"</c>) in the v1 files list so both interpretations
    /// see identical piece-aligned content. Default <c>false</c> (v2-only).
    /// </summary>
    public bool Hybrid { get; set; }
}
