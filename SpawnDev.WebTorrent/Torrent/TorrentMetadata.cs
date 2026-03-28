using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Torrent;

/// <summary>
/// Parsed torrent metadata (from .torrent file or magnet URI).
/// Contains everything needed to join a swarm and verify downloaded data.
/// </summary>
public class TorrentMetadata
{
    /// <summary>20-byte SHA-1 hash identifying this torrent.</summary>
    public byte[] InfoHash { get; set; } = Array.Empty<byte>();

    /// <summary>Hex string representation of InfoHash.</summary>
    public string InfoHashHex => Convert.ToHexString(InfoHash).ToLowerInvariant();

    /// <summary>Torrent name (from metadata or magnet dn= parameter).</summary>
    public string Name { get; set; } = "";

    /// <summary>Total size of all files in bytes.</summary>
    public long TotalLength { get; set; }

    /// <summary>Size of each piece in bytes (except possibly the last piece).</summary>
    public int PieceLength { get; set; }

    /// <summary>Number of pieces.</summary>
    public int PieceCount => PieceLength > 0 ? (int)((TotalLength + PieceLength - 1) / PieceLength) : 0;

    /// <summary>SHA-1 hashes for each piece (20 bytes each). Used to verify downloaded data.</summary>
    public byte[][] PieceHashes { get; set; } = Array.Empty<byte[]>();

    /// <summary>Files in this torrent.</summary>
    public TorrentFile[] Files { get; set; } = Array.Empty<TorrentFile>();

    /// <summary>Tracker announce URLs.</summary>
    public string[][] AnnounceList { get; set; } = Array.Empty<string[]>();

    /// <summary>Web seed URLs (BEP 17/19).</summary>
    public string[] UrlList { get; set; } = Array.Empty<string>();

    /// <summary>Creation date (if present).</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Comment (if present).</summary>
    public string? Comment { get; set; }

    /// <summary>Created by (if present).</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Whether this is a private torrent (no DHT/PEX).</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Raw info dictionary bytes (for computing info hash).</summary>
    public byte[]? InfoDictBytes { get; set; }

    /// <summary>Original .torrent file bytes (saved on parse/create).</summary>
    public byte[]? OriginalTorrentBytes { get; set; }

    /// <summary>Verify a downloaded piece against its hash.</summary>
    public bool VerifyPiece(int index, byte[] data)
    {
        if (index < 0 || index >= PieceHashes.Length) return false;
        var hash = SHA1.HashData(data);
        return hash.AsSpan().SequenceEqual(PieceHashes[index]);
    }
}

/// <summary>
/// A single file within a torrent.
/// </summary>
public class TorrentFile
{
    /// <summary>File path within the torrent (may include directory components).</summary>
    public string Path { get; set; } = "";

    /// <summary>File name (last component of Path).</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>File size in bytes.</summary>
    public long Length { get; set; }

    /// <summary>Byte offset of this file within the concatenated torrent data.</summary>
    public long Offset { get; set; }

    /// <summary>Index of the first piece containing this file's data.</summary>
    public int StartPiece { get; set; }

    /// <summary>Index of the last piece containing this file's data.</summary>
    public int EndPiece { get; set; }
}
