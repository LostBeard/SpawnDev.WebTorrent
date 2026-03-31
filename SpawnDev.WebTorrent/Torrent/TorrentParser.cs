using System.Security.Cryptography;
using System.Text;
using SpawnDev.WebTorrent.Bencode;

namespace SpawnDev.WebTorrent.Torrent;

/// <summary>
/// Parses .torrent files (bencoded dictionaries) into TorrentMetadata.
/// Also generates .torrent files for seeding.
///
/// .torrent structure:
///   { "announce": tracker_url,
///     "announce-list": [[tracker1], [tracker2]],
///     "info": { "name": ..., "piece length": ..., "pieces": ...,
///               "length": ... (single file) OR "files": [...] (multi-file) },
///     "url-list": [web_seed_urls] }
/// </summary>
public static class TorrentParser
{
    /// <summary>
    /// Parse a .torrent file from raw bytes.
    /// </summary>
    public static TorrentMetadata Parse(byte[] torrentBytes)
    {
        // Decode top-level dictionary, tracking raw bytes of "info" for hash computation
        var (dict, rawKeys, _) = BencodeDecoder.DecodeDictionaryWithRawKeys(
            torrentBytes, 0, "info");

        var metadata = new TorrentMetadata();
        metadata.OriginalTorrentBytes = torrentBytes;

        // Announce (single tracker)
        if (dict.TryGetValue("announce", out var announce) && announce is byte[] announceBytes)
        {
            var announceUrl = Encoding.UTF8.GetString(announceBytes);
            metadata.AnnounceList = new[] { new[] { announceUrl } };
        }

        // Announce-list (multiple trackers, tiered)
        if (dict.TryGetValue("announce-list", out var announceList) && announceList is List<object> tiers)
        {
            var tierList = new List<string[]>();
            foreach (var tier in tiers)
            {
                if (tier is List<object> urls)
                {
                    var tierUrls = urls
                        .Where(u => u is byte[])
                        .Select(u => Encoding.UTF8.GetString((byte[])u))
                        .ToArray();
                    if (tierUrls.Length > 0) tierList.Add(tierUrls);
                }
            }
            if (tierList.Count > 0) metadata.AnnounceList = tierList.ToArray();
        }

        // Web seeds (url-list)
        if (dict.TryGetValue("url-list", out var urlList))
        {
            if (urlList is List<object> urls)
                metadata.UrlList = urls.Where(u => u is byte[])
                    .Select(u => Encoding.UTF8.GetString((byte[])u)).ToArray();
            else if (urlList is byte[] singleUrl)
                metadata.UrlList = new[] { Encoding.UTF8.GetString(singleUrl) };
        }

        // Comment
        if (dict.TryGetValue("comment", out var comment) && comment is byte[] commentBytes)
            metadata.Comment = Encoding.UTF8.GetString(commentBytes);

        // Created by
        if (dict.TryGetValue("created by", out var createdBy) && createdBy is byte[] createdByBytes)
            metadata.CreatedBy = Encoding.UTF8.GetString(createdByBytes);

        // Creation date
        if (dict.TryGetValue("creation date", out var creationDate) && creationDate is long timestamp)
            metadata.CreationDate = DateTimeOffset.FromUnixTimeSeconds(timestamp);

        // Info dictionary
        if (!dict.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
            throw new InvalidOperationException("Missing or invalid 'info' dictionary in torrent file");

        // Compute info hash from raw info dictionary bytes
        if (rawKeys.TryGetValue("info", out var infoRaw))
        {
            metadata.InfoDictBytes = new byte[infoRaw.length];
            Array.Copy(torrentBytes, infoRaw.offset, metadata.InfoDictBytes, 0, infoRaw.length);
            metadata.InfoHash = SHA1.HashData(metadata.InfoDictBytes);
        }

        // Name
        if (info.TryGetValue("name", out var name) && name is byte[] nameBytes)
            metadata.Name = Encoding.UTF8.GetString(nameBytes);

        // Piece length
        if (info.TryGetValue("piece length", out var pieceLen) && pieceLen is long pl)
            metadata.PieceLength = (int)pl;

        // Private flag
        if (info.TryGetValue("private", out var priv) && priv is long privVal)
            metadata.IsPrivate = privVal != 0;

        // Total length — parse BEFORE pieces so auto-detection can use it
        long totalLength = 0;
        if (info.TryGetValue("length", out var lengthVal) && lengthVal is long singleLen)
            totalLength = singleLen;
        else if (info.TryGetValue("files", out var filesVal) && filesVal is List<object> filesList)
            totalLength = filesList.OfType<Dictionary<string, object>>()
                .Sum(f => f.TryGetValue("length", out var fl) && fl is long flen ? flen : 0);

        // Pieces (concatenated hashes: 20 bytes each for SHA-1, 32 bytes for SHA-256)
        if (info.TryGetValue("pieces", out var pieces) && pieces is byte[] piecesBytes)
        {
            // Auto-detect hash size from piece count match
            int hashSize = 20; // default SHA-1
            if (piecesBytes.Length % 32 == 0 && piecesBytes.Length % 20 != 0)
            {
                hashSize = 32;
            }
            else if (piecesBytes.Length % 32 == 0 && piecesBytes.Length % 20 == 0
                && metadata.PieceLength > 0 && totalLength > 0)
            {
                int expectedCount = (int)((totalLength + metadata.PieceLength - 1) / metadata.PieceLength);
                if (piecesBytes.Length / 32 == expectedCount)
                    hashSize = 32;
            }

            int count = piecesBytes.Length / hashSize;
            metadata.PieceHashes = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                metadata.PieceHashes[i] = new byte[hashSize];
                Array.Copy(piecesBytes, i * hashSize, metadata.PieceHashes[i], 0, hashSize);
            }
        }

        // Files: single-file or multi-file mode
        if (info.TryGetValue("length", out var length) && length is long singleLength)
        {
            // Single-file torrent
            metadata.TotalLength = singleLength;
            metadata.Files = new[]
            {
                new TorrentFile
                {
                    Path = metadata.Name,
                    Length = singleLength,
                    Offset = 0,
                    StartPiece = 0,
                    EndPiece = metadata.PieceCount - 1,
                }
            };
        }
        else if (info.TryGetValue("files", out var files) && files is List<object> fileList)
        {
            // Multi-file torrent
            var parsedFiles = new List<TorrentFile>();
            long offset = 0;

            foreach (var fileObj in fileList)
            {
                if (fileObj is not Dictionary<string, object> fileDict) continue;

                long fileLength = 0;
                if (fileDict.TryGetValue("length", out var fl) && fl is long fll)
                    fileLength = fll;

                string filePath = metadata.Name;
                if (fileDict.TryGetValue("path", out var pathObj) && pathObj is List<object> pathParts)
                {
                    var parts = pathParts.Where(p => p is byte[])
                        .Select(p => Encoding.UTF8.GetString((byte[])p));
                    filePath = Path.Combine(metadata.Name, Path.Combine(parts.ToArray()));
                }

                int startPiece = metadata.PieceLength > 0 ? (int)(offset / metadata.PieceLength) : 0;
                int endPiece = metadata.PieceLength > 0 ? (int)((offset + fileLength - 1) / metadata.PieceLength) : 0;

                parsedFiles.Add(new TorrentFile
                {
                    Path = filePath,
                    Length = fileLength,
                    Offset = offset,
                    StartPiece = startPiece,
                    EndPiece = endPiece,
                });

                offset += fileLength;
            }

            metadata.TotalLength = offset;
            metadata.Files = parsedFiles.ToArray();
        }

        metadata.OriginalTorrentBytes = torrentBytes;
        return metadata;
    }

    /// <summary>
    /// Parse raw info dictionary bytes (from ut_metadata exchange) into TorrentMetadata.
    /// Verifies the info hash matches the expected hash.
    /// </summary>
    public static TorrentMetadata? ParseInfoDict(byte[] infoDictBytes, byte[] expectedInfoHash)
    {
        var hash = SHA1.HashData(infoDictBytes);
        if (!hash.SequenceEqual(expectedInfoHash))
            return null; // hash mismatch

        // Wrap in a minimal .torrent structure: d4:infod...ee
        var prefix = Encoding.ASCII.GetBytes("d4:info");
        var suffix = Encoding.ASCII.GetBytes("e");
        var torrentBytes = new byte[prefix.Length + infoDictBytes.Length + suffix.Length];
        Array.Copy(prefix, torrentBytes, prefix.Length);
        Array.Copy(infoDictBytes, 0, torrentBytes, prefix.Length, infoDictBytes.Length);
        Array.Copy(suffix, 0, torrentBytes, prefix.Length + infoDictBytes.Length, suffix.Length);

        return Parse(torrentBytes);
    }

    /// <summary>
    /// Parse a magnet URI into partial metadata (info hash + trackers + name).
    /// Full metadata must be obtained via ut_metadata extension from peers.
    /// </summary>
    public static TorrentMetadata ParseMagnet(string magnetUri)
    {
        var metadata = new TorrentMetadata();
        var trackers = new List<string>();

        if (!magnetUri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Not a magnet URI");

        var query = magnetUri.Substring("magnet:?".Length);
        foreach (var param in query.Split('&'))
        {
            var eqIdx = param.IndexOf('=');
            if (eqIdx < 0) continue;
            var key = param.Substring(0, eqIdx);
            var value = Uri.UnescapeDataString(param.Substring(eqIdx + 1).Replace('+', ' '));

            switch (key)
            {
                case "xt" when value.StartsWith("urn:btih:"):
                    var hashStr = value.Substring("urn:btih:".Length);
                    metadata.InfoHash = hashStr.Length == 40
                        ? Convert.FromHexString(hashStr)
                        : hashStr.Length == 32
                            ? Base32Decode(hashStr)
                            : throw new ArgumentException($"Invalid info hash: {hashStr}");
                    break;
                case "dn":
                    metadata.Name = value;
                    break;
                case "tr":
                    trackers.Add(value);
                    break;
                case "ws":
                    metadata.UrlList = metadata.UrlList.Append(value).ToArray();
                    break;
                case "so": // BEP 53: file selection indices (comma-separated)
                    metadata.SelectedFileIndices = value.Split(',')
                        .Select(s => s.Trim())
                        .Where(s => int.TryParse(s, out _))
                        .Select(int.Parse)
                        .ToArray();
                    break;
                case "xs": // Exact source (.torrent URL)
                    metadata.ExactSource = value;
                    break;
            }
        }

        if (trackers.Count > 0)
            metadata.AnnounceList = trackers.Select(t => new[] { t }).ToArray();

        return metadata;
    }

    /// <summary>Decode Base32 (used by some magnet URIs for info hash).</summary>
    private static byte[] Base32Decode(string input)
    {
        input = input.ToUpperInvariant();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bitsLeft = 0;

        foreach (char c in input)
        {
            int val = alphabet.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }
        return output.ToArray();
    }
}
