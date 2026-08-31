using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace SpawnDev.WebTorrent.Server.HuggingFace;

/// <summary>The archive containers this hub can read a single member out of.</summary>
public enum ArchiveKind
{
    None,
    Tar,
    TarGz,
    TarBz2,
    Zip,
}

/// <summary>
/// Pulls ONE file out of a remote archive and caches it, so an archived model becomes a plain URL.
/// </summary>
/// <remarks>
/// <para>
/// Models keep arriving packaged: sherpa-onnx ships every one as <c>.tar.bz2</c>, others zip. Hosting each
/// file we happen to need solves one model and leaves the next one blocked, so the hub learns to open the
/// container instead.
/// </para>
/// <para>
/// ⚠️ WHY THIS IS SERVER-SIDE, when the client already has random-access streams. A <c>.tar</c> or
/// <c>.zip</c> IS randomly accessible - tar by walking 512-byte headers and seeking past member data, zip
/// via its central directory - and for those a client-side reader over the lazy-hash stream genuinely
/// avoids downloading the rest. A <c>.tar.bz2</c> or <c>.tar.gz</c> is NOT: you cannot seek into a
/// compressed stream without an index, so the whole archive must be fetched and decompressed no matter who
/// does it. Doing that once on the hub - which has the disk and the CPU - and caching the members is the
/// difference between one machine paying it and every visitor paying it. The client-side reader remains
/// worth having for the uncompressed cases; it just cannot cover this one.
/// </para>
/// <para>
/// ⚠️ Member paths are matched EXACTLY as they appear in the archive, and a member is rejected if its name
/// would escape the cache directory (<c>..</c> segments, absolute paths, drive letters). Archive entries
/// are attacker-controlled data - that is the "zip slip" class - and this cache is written from them.
/// </para>
/// </remarks>
public static class ArchiveMemberExtractor
{
    /// <summary>Guess the container from the URL's path. Query strings are ignored.</summary>
    public static ArchiveKind DetectKind(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".tar.bz2") || path.EndsWith(".tbz2") || path.EndsWith(".tbz")) return ArchiveKind.TarBz2;
        if (path.EndsWith(".tar.gz") || path.EndsWith(".tgz")) return ArchiveKind.TarGz;
        if (path.EndsWith(".tar")) return ArchiveKind.Tar;
        if (path.EndsWith(".zip")) return ArchiveKind.Zip;
        return ArchiveKind.None;
    }

    /// <summary>A cache filename for one member of one archive.</summary>
    public static string MemberCacheName(Uri archive, string member)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(archive.AbsoluteUri + "#" + member)))[..16];
        var leaf = Path.GetFileName(member);
        if (string.IsNullOrEmpty(leaf)) leaf = "member";
        foreach (var c in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
        if (leaf.Length > 60) leaf = leaf[^60..];
        return $"{hash}_{leaf}";
    }

    /// <summary>
    /// Whether a member path is safe to derive a cache filename from.
    /// </summary>
    /// <remarks>
    /// The cache name is a hash, so a hostile path cannot pick the file that gets written - but rejecting
    /// these outright keeps the failure honest and near the input, rather than relying on the hash to
    /// launder it. Names come from the archive, which is data we did not write.
    /// </remarks>
    public static bool IsSafeMemberPath(string member)
        => !string.IsNullOrWhiteSpace(member)
           && !Path.IsPathRooted(member)
           && !member.Contains("..", StringComparison.Ordinal)
           && !member.Contains(':', StringComparison.Ordinal);

    /// <summary>Names of every member, in archive order.</summary>
    public static async Task<List<ArchiveEntryInfo>> ListAsync(
        string archivePath, ArchiveKind kind, CancellationToken ct = default)
    {
        var entries = new List<ArchiveEntryInfo>();
        if (kind == ArchiveKind.Zip)
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var e in zip.Entries)
                if (e.Length > 0 || !e.FullName.EndsWith('/'))
                    entries.Add(new ArchiveEntryInfo(e.FullName, e.Length));
            return entries;
        }

        await using var raw = File.OpenRead(archivePath);
        await using var decompressed = Decompress(raw, kind);
        await using var tar = new TarReader(decompressed);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                entries.Add(new ArchiveEntryInfo(entry.Name, entry.Length));
        return entries;
    }

    /// <summary>
    /// Write one member to <paramref name="destinationPath"/>. Returns false when the member is absent.
    /// </summary>
    /// <remarks>
    /// Writes to a temporary file and moves it into place, so an interrupted extraction cannot leave a
    /// TRUNCATED member looking like a complete cache hit - which would then be served forever.
    /// </remarks>
    public static async Task<bool> ExtractAsync(
        string archivePath, ArchiveKind kind, string member, string destinationPath,
        CancellationToken ct = default)
    {
        var temp = destinationPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        if (kind == ArchiveKind.Zip)
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry(member);
            if (entry == null) return false;
            await using (var src = entry.Open())
            await using (var dst = File.Create(temp))
                await src.CopyToAsync(dst, ct);
            File.Move(temp, destinationPath, overwrite: true);
            return true;
        }

        await using (var raw = File.OpenRead(archivePath))
        await using (var decompressed = Decompress(raw, kind))
        await using (var tar = new TarReader(decompressed))
        {
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (!string.Equals(entry.Name, member, StringComparison.Ordinal)) continue;
                if (entry.DataStream == null) return false;
                await using (var dst = File.Create(temp))
                    await entry.DataStream.CopyToAsync(dst, ct);
                File.Move(temp, destinationPath, overwrite: true);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Wrap the raw archive stream in whatever decompressor the container needs.
    /// </summary>
    /// <remarks>
    /// bzip2 is the one the BCL does not provide, and it is exactly the one sherpa-onnx uses - hence the
    /// single third-party dependency here. Tar and gzip and zip are all in-box.
    /// </remarks>
    private static Stream Decompress(Stream raw, ArchiveKind kind) => kind switch
    {
        ArchiveKind.Tar => raw,
        ArchiveKind.TarGz => new GZipStream(raw, CompressionMode.Decompress),
        ArchiveKind.TarBz2 => new BZip2InputStream(raw),
        _ => throw new NotSupportedException($"{kind} is not a tar container"),
    };
}

/// <summary>One member of an archive.</summary>
public record ArchiveEntryInfo(string Name, long Size);
