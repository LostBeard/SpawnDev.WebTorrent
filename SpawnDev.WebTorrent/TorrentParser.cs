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
        var (dict, rawKeys, _) = BencodeDecoder.DecodeDictionaryWithRawKeys(torrentBytes, 0, "info", "piece layers");
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

        // Web seeds - BEP 19 (url-list)
        if (dict.TryGetValue("url-list", out var urlList))
        {
            if (urlList is List<object> urls)
                metadata.UrlList = urls.OfType<byte[]>().Select(u => Encoding.UTF8.GetString(u)).ToArray();
            else if (urlList is byte[] singleUrl)
                metadata.UrlList = new[] { Encoding.UTF8.GetString(singleUrl) };
        }

        // HTTP seeds - BEP 17 Hoffman-style (httpseeds)
        if (dict.TryGetValue("httpseeds", out var httpSeeds))
        {
            if (httpSeeds is List<object> seeds)
                metadata.HttpSeeds = seeds.OfType<byte[]>().Select(u => Encoding.UTF8.GetString(u)).ToArray();
            else if (httpSeeds is byte[] singleSeed)
                metadata.HttpSeeds = new[] { Encoding.UTF8.GetString(singleSeed) };
        }

        // Info dictionary
        if (!dict.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
            throw new InvalidOperationException("Missing or invalid 'info' dictionary");

        // BEP 52 v2 detection: info dict has a "meta version" key with value 2.
        // Hybrid torrents also carry this (alongside the v1 "pieces" key); v2-only torrents
        // do not. Detecting here lets later parsing decide which shape to read.
        bool isV2 = info.TryGetValue("meta version", out var metaVer) && metaVer is long mv && mv == 2;
        if (isV2) metadata.MetaVersion = 2;

        // Compute info hash(es) from raw bytes. v1 = SHA-1, v2 = SHA-256. Hybrid torrents get
        // both populated; v2-only torrents leave InfoHash empty.
        if (rawKeys.TryGetValue("info", out var infoRaw))
        {
            var infoBytes = new byte[infoRaw.length];
            Array.Copy(torrentBytes, infoRaw.offset, infoBytes, 0, infoRaw.length);
            metadata.InfoDictBytes = infoBytes;
            if (isV2)
            {
                metadata.V2InfoHash = Convert.ToHexString(SHA256.HashData(infoBytes)).ToLowerInvariant();
            }
            // v1 info hash is meaningful whenever the info dict carries a "pieces" key (v1 or
            // hybrid torrents). Pure v2 torrents skip it - an SHA-1 of a v2 info dict is not
            // the v1 infohash of anything and would be misleading to surface.
            if (info.ContainsKey("pieces"))
            {
                metadata.InfoHash = Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();
            }
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

        // BEP 52 v2 extensions: parse file tree, per-file Merkle roots, and piece layers.
        // Runs last so v2 data overrides any v1 Files[] populated above (hybrid torrents must
        // agree on file list between v1 and v2 anyway - v2 is the canonical source).
        if (isV2 && info.TryGetValue("file tree", out var fileTreeObj)
            && fileTreeObj is Dictionary<string, object> fileTree)
        {
            var v2Files = new List<TorrentFileInfo>();
            var v2Roots = new List<byte[]>();
            long cumulativeOffset = 0;
            ParseV2FileTree(fileTree, currentPath: "", v2Files, v2Roots, ref cumulativeOffset);

            if (v2Files.Count > 0)
            {
                metadata.Files = v2Files.ToArray();
                metadata.FileRoots = v2Roots.ToArray();
                metadata.TotalLength = cumulativeOffset;
            }
        }

        // piece layers lives at the TOP level of the torrent dict (not in info). Re-decode it
        // with raw-byte keys because the keys are 32-byte SHA-256 roots that generally are
        // not valid UTF-8 and would be corrupted by the string-keyed decoder.
        if (isV2 && rawKeys.TryGetValue("piece layers", out var pieceLayersRaw))
        {
            var (entries, _) = BencodeDecoder.DecodeDictionaryRawKeys(torrentBytes, pieceLayersRaw.offset);
            foreach (var kvp in entries)
            {
                if (kvp.Value is byte[] concat)
                    metadata.PieceLayers[kvp.Key] = concat;
            }
        }

        // Populate PieceHashes uniformly so PieceHashAlgorithm and downstream code see the same
        // shape as v1. Phase 2a supports single-file v2 only; multi-file Phase 2b will extend this.
        if (isV2 && metadata.FileRoots.Length > 0)
        {
            var root = metadata.FileRoots[0];
            if (metadata.PieceLayers.TryGetValue(root, out var concat))
            {
                int count = concat.Length / 32;
                var hashes = new byte[count][];
                for (int i = 0; i < count; i++)
                {
                    hashes[i] = new byte[32];
                    Buffer.BlockCopy(concat, i * 32, hashes[i], 0, 32);
                }
                metadata.PieceHashes = hashes;
                metadata.PieceCount = count;
            }
            else
            {
                // Single-piece file - the pieces root IS the single piece hash.
                metadata.PieceHashes = new[] { root };
                metadata.PieceCount = 1;
            }
        }

        metadata.OriginalTorrentBytes = torrentBytes;
        return metadata;
    }

    /// <summary>
    /// Recursively walks a BEP 52 file tree dict and appends discovered files to the given lists.
    /// File nodes are identified by the presence of an empty-string ("") key whose value is a
    /// dict with "length" and "pieces root"; directory nodes contain keyed child entries.
    /// </summary>
    private static void ParseV2FileTree(
        Dictionary<string, object> tree,
        string currentPath,
        List<TorrentFileInfo> files,
        List<byte[]> roots,
        ref long offset)
    {
        // BEP 52 requires dict keys be sorted as raw byte strings. Ordinal sort over the
        // UTF-8-decoded strings is the right approximation for all filenames we will see.
        foreach (var entry in tree.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (entry.Value is not Dictionary<string, object> node) continue;

            if (node.TryGetValue("", out var leafObj) && leafObj is Dictionary<string, object> fileInfo)
            {
                // File leaf.
                long length = 0;
                byte[] root = Array.Empty<byte>();
                if (fileInfo.TryGetValue("length", out var lenObj) && lenObj is long len) length = len;
                if (fileInfo.TryGetValue("pieces root", out var rootObj) && rootObj is byte[] rootBytes) root = rootBytes;

                var filePath = string.IsNullOrEmpty(currentPath) ? entry.Key : $"{currentPath}/{entry.Key}";
                files.Add(new TorrentFileInfo
                {
                    Path = filePath,
                    Name = System.IO.Path.GetFileName(filePath),
                    Length = length,
                    Offset = offset,
                });
                roots.Add(root);
                offset += length;
            }
            else
            {
                // Directory node - recurse.
                var subPath = string.IsNullOrEmpty(currentPath) ? entry.Key : $"{currentPath}/{entry.Key}";
                ParseV2FileTree(node, subPath, files, roots, ref offset);
            }
        }
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
