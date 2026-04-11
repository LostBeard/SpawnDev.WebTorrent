using System.Security.Cryptography;
using System.Text;
using SpawnDev.WebTorrent.Bencode;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Parses .torrent files (bencoded dictionaries) into TorrentMetadata.
/// Adapted from original TorrentParser.cs to match our rewrite's type system.
/// </summary>
public static class TorrentParser
{
    public static TorrentMetadata Parse(byte[] torrentBytes)
    {
        var (dict, rawKeys, _) = BencodeDecoder.DecodeDictionaryWithRawKeys(torrentBytes, 0, "info");
        var metadata = new TorrentMetadata();

        // Announce
        if (dict.TryGetValue("announce", out var announce) && announce is byte[] announceBytes)
            metadata.AnnounceUrls = new[] { Encoding.UTF8.GetString(announceBytes) };

        // Announce-list (tiered)
        if (dict.TryGetValue("announce-list", out var announceList) && announceList is List<object> tiers)
        {
            var urls = new List<string>();
            foreach (var tier in tiers)
            {
                if (tier is List<object> tierUrls)
                    urls.AddRange(tierUrls.OfType<byte[]>().Select(u => Encoding.UTF8.GetString(u)));
            }
            if (urls.Count > 0)
                metadata.AnnounceUrls = urls.ToArray();
        }

        // Web seeds (url-list)
        if (dict.TryGetValue("url-list", out var urlList))
        {
            if (urlList is List<object> urls)
                metadata.UrlList = urls.OfType<byte[]>().Select(u => Encoding.UTF8.GetString(u)).ToArray();
            else if (urlList is byte[] singleUrl)
                metadata.UrlList = new[] { Encoding.UTF8.GetString(singleUrl) };
        }

        // Info dictionary
        if (!dict.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
            throw new InvalidOperationException("Missing or invalid 'info' dictionary");

        // Compute info hash from raw bytes
        if (rawKeys.TryGetValue("info", out var infoRaw))
        {
            var infoBytes = new byte[infoRaw.length];
            Array.Copy(torrentBytes, infoRaw.offset, infoBytes, 0, infoRaw.length);
            metadata.InfoHash = Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();
        }

        // Name
        if (info.TryGetValue("name", out var name) && name is byte[] nameBytes)
            metadata.Name = Encoding.UTF8.GetString(nameBytes);

        // Piece length
        if (info.TryGetValue("piece length", out var pieceLen) && pieceLen is long pl)
            metadata.PieceLength = (int)pl;

        // Private
        if (info.TryGetValue("private", out var priv) && priv is long privVal)
            metadata.IsPrivate = privVal != 0;

        // Total length (for hash size auto-detection)
        long totalLength = 0;
        if (info.TryGetValue("length", out var lengthVal) && lengthVal is long singleLen)
            totalLength = singleLen;
        else if (info.TryGetValue("files", out var filesVal) && filesVal is List<object> filesList)
            totalLength = filesList.OfType<Dictionary<string, object>>()
                .Sum(f => f.TryGetValue("length", out var fl) && fl is long flen ? flen : 0);

        // Pieces (concatenated hashes)
        if (info.TryGetValue("pieces", out var pieces) && pieces is byte[] piecesBytes)
        {
            int hashSize = 20; // SHA-1 default
            if (piecesBytes.Length % 32 == 0 && piecesBytes.Length % 20 != 0)
                hashSize = 32;
            else if (piecesBytes.Length % 32 == 0 && piecesBytes.Length % 20 == 0
                && metadata.PieceLength > 0 && totalLength > 0)
            {
                int expectedCount = (int)((totalLength + metadata.PieceLength - 1) / metadata.PieceLength);
                if (piecesBytes.Length / 32 == expectedCount)
                    hashSize = 32;
            }

            if (piecesBytes.Length % hashSize != 0)
                throw new InvalidOperationException($"Pieces length {piecesBytes.Length} not divisible by {hashSize}");

            int count = piecesBytes.Length / hashSize;
            metadata.PieceHashes = new byte[count][];
            metadata.PieceCount = count;
            for (int i = 0; i < count; i++)
            {
                metadata.PieceHashes[i] = new byte[hashSize];
                Array.Copy(piecesBytes, i * hashSize, metadata.PieceHashes[i], 0, hashSize);
            }
        }

        // Files
        if (info.TryGetValue("length", out var length) && length is long singleLength)
        {
            metadata.TotalLength = singleLength;
            metadata.Files = new[]
            {
                new TorrentFileInfo { Path = metadata.Name, Length = singleLength, Offset = 0 }
            };
        }
        else if (info.TryGetValue("files", out var files) && files is List<object> fileList)
        {
            var parsedFiles = new List<TorrentFileInfo>();
            long offset = 0;
            foreach (var fileObj in fileList)
            {
                if (fileObj is not Dictionary<string, object> fileDict) continue;
                long fileLength = fileDict.TryGetValue("length", out var fl) && fl is long fll ? fll : 0;
                string filePath = metadata.Name;
                if (fileDict.TryGetValue("path", out var pathObj) && pathObj is List<object> pathParts)
                {
                    var parts = pathParts.OfType<byte[]>().Select(p => Encoding.UTF8.GetString(p));
                    filePath = Path.Combine(metadata.Name, Path.Combine(parts.ToArray()));
                }
                parsedFiles.Add(new TorrentFileInfo { Path = filePath, Name = Path.GetFileName(filePath), Length = fileLength, Offset = offset });
                offset += fileLength;
            }
            metadata.TotalLength = offset;
            metadata.Files = parsedFiles.ToArray();
        }

        metadata.OriginalTorrentBytes = torrentBytes;
        return metadata;
    }

    /// <summary>Parse raw info dict bytes (from ut_metadata) with hash verification.</summary>
    public static TorrentMetadata? ParseInfoDict(byte[] infoDictBytes, byte[] expectedInfoHash)
    {
        var hash = SHA1.HashData(infoDictBytes);
        if (!hash.SequenceEqual(expectedInfoHash)) return null;

        var prefix = "d4:info"u8.ToArray();
        var suffix = "e"u8.ToArray();
        var torrentBytes = new byte[prefix.Length + infoDictBytes.Length + suffix.Length];
        prefix.CopyTo(torrentBytes, 0);
        infoDictBytes.CopyTo(torrentBytes, prefix.Length);
        suffix.CopyTo(torrentBytes, prefix.Length + infoDictBytes.Length);

        var metadata = Parse(torrentBytes);
        if (metadata != null)
            metadata.OriginalTorrentBytes = torrentBytes;
        return metadata;
    }
}
