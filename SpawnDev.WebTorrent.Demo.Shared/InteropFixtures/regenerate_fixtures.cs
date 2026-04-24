#!/usr/bin/env dotnet
// Regenerates the libtorrent 2.0 v2 reference fixtures + manifest.json next to this file.
// Run with: dotnet run regenerate_fixtures.cs
//
// Why this exists: our BEP 52 external-interop test corpus
// (WebTorrentTestBase.LibtorrentInteropTests.cs) parses v2 torrents from
// libtorrent's own test suite to prove our parser agrees byte-for-byte with the
// reference C++ implementation. This script pulls those torrents fresh from
// libtorrent's GitHub and regenerates the manifest with meta_version,
// piece_length, v2 info hash, and fixture-shape flags per file.
//
// Pinned to libtorrent's RC_2_0 branch so regeneration is deterministic even if
// libtorrent's master branch evolves. Bump the BranchRef below if you want a
// newer corpus.
//
// Single-file script (no csproj) - works on .NET 10+ via `dotnet run <file>.cs`.
// No NuGet deps; bencode is parsed inline since we need ~50 lines of it and
// embedding SpawnDev.WebTorrent as a script dependency would drag the whole
// client in.

using System.Security.Cryptography;
using System.Text;

const string BranchRef = "RC_2_0";
const string BaseUrl = $"https://raw.githubusercontent.com/arvidn/libtorrent/{BranchRef}/test/test_torrents";

var fixtures = new[]
{
    "v2.torrent",
    "v2_multipiece_file.torrent",
    "v2_only.torrent",
    "v2_hybrid.torrent",
};

var outDir = AppContext.BaseDirectory;
// `dotnet run file.cs` runs from a temp build dir — resolve our source dir from the script file.
var scriptPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith("regenerate_fixtures.cs"));
if (scriptPath != null) outDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
else outDir = Directory.GetCurrentDirectory();

Console.WriteLine($"Output directory: {outDir}");

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("User-Agent", "SpawnDev.WebTorrent-fixture-regen/1.0");

var manifest = new List<FixtureEntry>();

foreach (var name in fixtures)
{
    var url = $"{BaseUrl}/{name}";
    Console.WriteLine($"Fetching {url} ...");
    var bytes = await http.GetByteArrayAsync(url);
    var outPath = Path.Combine(outDir, name);
    await File.WriteAllBytesAsync(outPath, bytes);

    var entry = Analyze(name, bytes);
    manifest.Add(entry);
    Console.WriteLine($"  {name}: {entry.Bytes} bytes, meta_version={entry.MetaVersion}, " +
                      $"piece_length={entry.PieceLength}, v2_info_hash={entry.V2InfoHashSha256Hex[..16]}...");
}

// Build JSON by hand — dotnet run's single-file mode disables reflection-based
// serialization, and the manifest shape is trivial enough that source-gen
// would be over-engineering.
var sb = new StringBuilder();
sb.Append("{\n  \"fixtures\": [\n");
for (int i = 0; i < manifest.Count; i++)
{
    var e = manifest[i];
    sb.Append("    {\n");
    sb.Append($"      \"file\": {JsonString(e.File)},\n");
    sb.Append($"      \"bytes\": {e.Bytes},\n");
    sb.Append($"      \"meta_version\": {e.MetaVersion},\n");
    sb.Append($"      \"piece_length\": {e.PieceLength},\n");
    sb.Append($"      \"name\": {JsonString(e.Name)},\n");
    sb.Append($"      \"has_v1_pieces\": {e.HasV1Pieces.ToString().ToLowerInvariant()},\n");
    sb.Append($"      \"has_v2_file_tree\": {e.HasV2FileTree.ToString().ToLowerInvariant()},\n");
    sb.Append($"      \"has_piece_layers_dict\": {e.HasPieceLayersDict.ToString().ToLowerInvariant()},\n");
    sb.Append($"      \"v2_info_hash_sha256_hex\": {JsonString(e.V2InfoHashSha256Hex)}\n");
    sb.Append(i == manifest.Count - 1 ? "    }\n" : "    },\n");
}
sb.Append("  ]\n}\n");
var manifestPath = Path.Combine(outDir, "libtorrent_reference_manifest.json");
await File.WriteAllTextAsync(manifestPath, sb.ToString());

static string JsonString(string s)
{
    var esc = new StringBuilder(s.Length + 2);
    esc.Append('"');
    foreach (var c in s)
    {
        switch (c)
        {
            case '"': esc.Append("\\\""); break;
            case '\\': esc.Append("\\\\"); break;
            case '\n': esc.Append("\\n"); break;
            case '\r': esc.Append("\\r"); break;
            case '\t': esc.Append("\\t"); break;
            default:
                if (c < 0x20) esc.Append($"\\u{(int)c:x4}");
                else esc.Append(c);
                break;
        }
    }
    esc.Append('"');
    return esc.ToString();
}
Console.WriteLine($"Wrote manifest: {manifestPath}");
Console.WriteLine("Done. Rebuild SpawnDev.WebTorrent.Demo.Shared so the embedded resources pick up the new bytes.");

static FixtureEntry Analyze(string name, byte[] bytes)
{
    // Top-level bencode: d...e, with a key "4:info" whose value we slice out and hash.
    var (infoStart, infoEnd) = LocateTopLevelInfoDict(bytes);
    var v2Hash = SHA256.HashData(new ArraySegment<byte>(bytes, infoStart, infoEnd - infoStart));

    // Parse the info dict as a bencode dict to pull meta_version, piece_length, name,
    // and v1/v2 shape flags. ReadValue supports offset-based parsing so we don't need
    // to copy the slice.
    int infoPos = infoStart;
    var info = (Dictionary<string, object>)ReadValue(bytes, ref infoPos);
    var metaVersion = info.TryGetValue("meta version", out var mv) && mv is long mvL ? (int)mvL : 0;
    var pieceLength = info.TryGetValue("piece length", out var pl) && pl is long plL ? (int)plL : 0;
    var nameStr = info.TryGetValue("name", out var nm) && nm is byte[] nmB
        ? Encoding.UTF8.GetString(nmB) : "";

    bool hasV1Pieces = info.ContainsKey("pieces");
    bool hasV2FileTree = info.ContainsKey("file tree");

    // piece_layers is a TOP-LEVEL key (outside info dict).
    int topPos = 0;
    var top = (Dictionary<string, object>)ReadValue(bytes, ref topPos);
    bool hasPieceLayersDict = top.ContainsKey("piece layers");

    return new FixtureEntry
    {
        File = name,
        Bytes = bytes.Length,
        MetaVersion = metaVersion,
        PieceLength = pieceLength,
        Name = nameStr,
        HasV1Pieces = hasV1Pieces,
        HasV2FileTree = hasV2FileTree,
        HasPieceLayersDict = hasPieceLayersDict,
        V2InfoHashSha256Hex = Convert.ToHexString(v2Hash).ToLowerInvariant(),
    };
}

// Walks the top-level dict, finds the "info" key, returns the [start, end) byte range
// of its value (a sub-dict). Caller SHA-256s that slice for the v2 info hash.
static (int start, int end) LocateTopLevelInfoDict(byte[] data)
{
    int pos = 0;
    Expect(data, ref pos, (byte)'d');
    while (pos < data.Length && data[pos] != (byte)'e')
    {
        var key = ReadString(data, ref pos);
        if (Encoding.UTF8.GetString(key) == "info")
        {
            int valueStart = pos;
            SkipValue(data, ref pos);
            int valueEnd = pos;
            return (valueStart, valueEnd);
        }
        SkipValue(data, ref pos);
    }
    throw new Exception("top-level 'info' key not found");
}

// Returns bencode dict as Dictionary<string, object> where values are: long, byte[] (string),
// List<object>, Dictionary<string, object>. Advances pos past the parsed value.
static object ReadValue(byte[] data, ref int pos)
{
    switch ((char)data[pos])
    {
        case 'i':
            pos++;
            int end = Array.IndexOf(data, (byte)'e', pos);
            var num = long.Parse(Encoding.ASCII.GetString(data, pos, end - pos));
            pos = end + 1;
            return num;
        case 'l':
            pos++;
            var list = new List<object>();
            while (data[pos] != (byte)'e') list.Add(ReadValue(data, ref pos));
            pos++; // skip 'e'
            return list;
        case 'd':
            pos++;
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            while (data[pos] != (byte)'e')
            {
                var key = Encoding.UTF8.GetString(ReadString(data, ref pos));
                dict[key] = ReadValue(data, ref pos);
            }
            pos++; // skip 'e'
            return dict;
        default:
            if (data[pos] >= (byte)'0' && data[pos] <= (byte)'9')
                return ReadString(data, ref pos);
            throw new Exception($"unexpected bencode token '{(char)data[pos]}' at {pos}");
    }
}

static byte[] ReadString(byte[] data, ref int pos)
{
    int colon = Array.IndexOf(data, (byte)':', pos);
    int len = int.Parse(Encoding.ASCII.GetString(data, pos, colon - pos));
    pos = colon + 1;
    var bytes = new byte[len];
    Buffer.BlockCopy(data, pos, bytes, 0, len);
    pos += len;
    return bytes;
}

static void SkipValue(byte[] data, ref int pos) => ReadValue(data, ref pos);

static void Expect(byte[] data, ref int pos, byte b)
{
    if (data[pos] != b) throw new Exception($"expected '{(char)b}' at {pos}, got '{(char)data[pos]}'");
    pos++;
}

file sealed class FixtureEntry
{
    public string File { get; set; } = "";
    public int Bytes { get; set; }
    public int MetaVersion { get; set; }
    public int PieceLength { get; set; }
    public string Name { get; set; } = "";
    public bool HasV1Pieces { get; set; }
    public bool HasV2FileTree { get; set; }
    public bool HasPieceLayersDict { get; set; }
    public string V2InfoHashSha256Hex { get; set; } = "";
}
