using System.Security.Cryptography;
using System.Text;
using SpawnDev.WebTorrent.Bencode;

namespace SpawnDev.WebTorrent.Torrent;

/// <summary>
/// Creates .torrent files from local files or byte arrays.
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

        var pieceHashes = new List<byte[]>();
        var buffer = new byte[pieceLength];
        bool useSha256 = options.HashAlgorithm == "SHA-256";
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, pieceLength), ct)) > 0)
        {
            pieceHashes.Add(useSha256
                ? SHA256.HashData(buffer.AsSpan(0, bytesRead))
                : SHA1.HashData(buffer.AsSpan(0, bytesRead)));
        }

        name = options.Name ?? name;

        return BuildTorrent(name, length, pieceLength, pieceHashes, options,
            new[] { new TorrentFile { Path = name, Length = length, Offset = 0,
                StartPiece = 0, EndPiece = pieceHashes.Count - 1 } });
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
            // Derive name from URL path
            var uri = new Uri(url);
            name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.TrimEnd('/') ?? "download.bin");
        }

        // Add the original URL as a web seed — it already serves the file
        options ??= new TorrentCreatorOptions();
        if (!options.WebSeeds.Contains(url))
        {
            // Build base URL for BEP 17 (directory, not full file path)
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
    /// Create a .torrent file from in-memory bytes.
    /// </summary>
    public static (byte[] torrentBytes, TorrentMetadata metadata) CreateFromBytes(
        string name, byte[] data, TorrentCreatorOptions? options = null)
    {
        options ??= new TorrentCreatorOptions();
        int pieceLength = options.PieceLength > 0
            ? options.PieceLength
            : CalculatePieceLength(data.Length);

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
            new[] { new TorrentFile { Path = name, Length = data.Length, Offset = 0,
                StartPiece = 0, EndPiece = pieceHashes.Count - 1 } });
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

        // Concatenate all file data for piece hashing
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
                System.Array.Copy(file.data, fileOffset, buffer, bufferFill, toCopy);
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
        // Hash any remaining partial piece
        if (bufferFill > 0)
        {
            pieceHashes.Add(useSha256
                ? SHA256.HashData(buffer.AsSpan(0, bufferFill))
                : SHA1.HashData(buffer.AsSpan(0, bufferFill)));
        }

        // Build TorrentFile entries with offsets
        var torrentFiles = new TorrentFile[files.Length];
        long offset = 0;
        for (int i = 0; i < files.Length; i++)
        {
            int startPiece = pieceLength > 0 ? (int)(offset / pieceLength) : 0;
            int endPiece = pieceLength > 0 ? (int)((offset + files[i].data.Length - 1) / pieceLength) : 0;
            torrentFiles[i] = new TorrentFile
            {
                Path = files[i].path,
                Length = files[i].data.Length,
                Offset = offset,
                StartPiece = startPiece,
                EndPiece = endPiece,
            };
            offset += files[i].data.Length;
        }

        return BuildTorrent(torrentName, totalLength, pieceLength, pieceHashes, options, torrentFiles);
    }

    private static (byte[] torrentBytes, TorrentMetadata metadata) BuildTorrent(
        string name, long totalLength, int pieceLength, List<byte[]> pieceHashes,
        TorrentCreatorOptions options, TorrentFile[] files)
    {
        // Concatenate piece hashes
        int hashSize = pieceHashes[0].Length;
        var piecesConcat = new byte[pieceHashes.Count * hashSize];
        for (int i = 0; i < pieceHashes.Count; i++)
            System.Array.Copy(pieceHashes[i], 0, piecesConcat, i * hashSize, hashSize);

        bool isMultiFile = files.Length > 1;

        // Build info dictionary (bencoded, raw bytes for hash computation)
        // Keys MUST be sorted alphabetically within the dict
        var infoParts = new List<byte>();
        infoParts.AddRange(Encoding.ASCII.GetBytes("d"));

        if (isMultiFile)
        {
            // Multi-file: "files" list instead of "length"
            infoParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("files")));
            infoParts.AddRange(Encoding.ASCII.GetBytes("l"));
            foreach (var file in files)
            {
                infoParts.AddRange(Encoding.ASCII.GetBytes("d"));
                AppendBencodeKV(infoParts, "length", file.Length);
                // "path" is a list of path components
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
            // Single-file: "length" key
            AppendBencodeKV(infoParts, "length", totalLength);
        }

        AppendBencodeKV(infoParts, "name", name);
        AppendBencodeKV(infoParts, "piece length", pieceLength);
        AppendBencodeKVBytes(infoParts, "pieces", piecesConcat);

        if (options.IsPrivate)
            AppendBencodeKV(infoParts, "private", 1L);

        infoParts.AddRange(Encoding.ASCII.GetBytes("e"));
        var infoBytes = infoParts.ToArray();
        var infoHash = SHA1.HashData(infoBytes);

        // Build top-level dictionary
        var topParts = new List<byte>();
        topParts.AddRange(Encoding.ASCII.GetBytes("d"));

        // Announce
        if (options.Trackers.Length > 0)
            AppendBencodeKV(topParts, "announce", options.Trackers[0]);

        // Announce-list (if multiple trackers)
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

        // Comment
        if (!string.IsNullOrEmpty(options.Comment))
            AppendBencodeKV(topParts, "comment", options.Comment);

        // Created by
        AppendBencodeKV(topParts, "created by", options.CreatedBy);

        // Creation date
        AppendBencodeKV(topParts, "creation date", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Info dictionary (raw bytes, not re-encoded)
        topParts.AddRange(BencodeEncoder.EncodeBytes(Encoding.UTF8.GetBytes("info")));
        topParts.AddRange(infoBytes);

        // URL list (web seeds)
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
            InfoHash = infoHash,
            InfoDictBytes = infoBytes,
            Name = name,
            TotalLength = totalLength,
            PieceLength = pieceLength,
            PieceHashes = pieceHashes.ToArray(),
            Files = files,
            AnnounceList = options.Trackers.Select(t => new[] { t }).ToArray(),
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
}
