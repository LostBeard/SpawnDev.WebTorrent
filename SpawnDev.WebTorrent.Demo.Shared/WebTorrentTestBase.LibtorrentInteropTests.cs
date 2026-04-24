using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// External-interop coverage: parse BEP 52 v2 reference .torrent files produced by
/// libtorrent 2.0 (Arvid Norberg's C++ BitTorrent library — one of the original
/// reference implementations of BEP 52). Fixtures are pulled from libtorrent's own
/// test corpus at
/// https://github.com/arvidn/libtorrent/tree/RC_2_0/test/test_torrents
/// and embedded as assembly resources in this project so the tests run unchanged in
/// both the Blazor WASM browser runtime and the desktop console runner under
/// PlaywrightMultiTest. See InteropFixtures\libtorrent_reference_manifest.json for
/// the expected v2 info hashes + piece layout per fixture.
///
/// What this proves: SpawnDev.WebTorrent.TorrentParser produces the same v2 info
/// hash (SHA-256 of the info dict) that libtorrent produced when it wrote the file,
/// across a hybrid v1+v2 torrent, a pure-v2 torrent, a single-piece file, and a
/// multi-piece file. If a future parser change ever shifts a byte in the info dict
/// encoding, these hashes will diverge and the test catches it — something
/// round-trip-only tests cannot, because they only prove the parser is
/// self-consistent.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task LibtorrentInterop_Hybrid_SinglePieceFile_MatchesV2InfoHash()
    {
        // Fixture: v2.torrent — hybrid v1+v2, single 64KB file, 64KB pieces (=> 1 piece).
        await AssertFixtureMatchesManifest(
            fixtureName: "v2.torrent",
            expectedHasV1InfoHash: true,
            expectedFileCount: 1);
    }

    [TestMethod]
    public async Task LibtorrentInterop_Hybrid_MultiPieceFile_MatchesV2InfoHash()
    {
        // Fixture: v2_multipiece_file.torrent — hybrid v1+v2, ~1MB file, 64KB pieces.
        // Exercises the piece layers dict path (file > piece size).
        await AssertFixtureMatchesManifest(
            fixtureName: "v2_multipiece_file.torrent",
            expectedHasV1InfoHash: true,
            expectedFileCount: 1);
    }

    [TestMethod]
    public async Task LibtorrentInterop_PureV2_MatchesV2InfoHashAndOmitsV1Hash()
    {
        // Fixture: v2_only.torrent — pure v2 (no v1 pieces, no v1 infohash).
        // The parser must populate V2InfoHash and leave InfoHash as empty string.
        await AssertFixtureMatchesManifest(
            fixtureName: "v2_only.torrent",
            expectedHasV1InfoHash: false,
            expectedFileCount: 1);
    }

    [TestMethod]
    public async Task LibtorrentInterop_HybridMultiFile_MatchesV2InfoHash()
    {
        // Fixture: v2_hybrid.torrent — libtorrent's bittorrent-v1-v2-hybrid-test corpus
        // fixture. Multi-file hybrid with 512KB pieces. Largest of the four (~89KB on
        // disk) because it carries the full piece layers dict inline. Proves the
        // parser handles real-world hybrid piece-layer dictionaries from third-party
        // encoders.
        //
        // expectedFileCount=null because we don't know the exact file count of
        // libtorrent's hybrid-test corpus fixture without inspecting it; we only
        // assert it's > 1 (multi-file).
        await AssertFixtureMatchesManifest(
            fixtureName: "v2_hybrid.torrent",
            expectedHasV1InfoHash: true,
            expectedFileCount: null);
    }

    // ---- helpers ----

    private static async Task AssertFixtureMatchesManifest(
        string fixtureName,
        bool expectedHasV1InfoHash,
        int? expectedFileCount)
    {
        var manifest = LoadManifest();
        var entry = manifest.Fixtures.FirstOrDefault(f => f.File == fixtureName)
            ?? throw new Exception($"manifest missing entry for {fixtureName}");

        var torrentBytes = LoadFixture(fixtureName);
        if (torrentBytes.Length != entry.Bytes)
            throw new Exception(
                $"{fixtureName}: loaded {torrentBytes.Length} bytes but manifest says {entry.Bytes} — " +
                $"fixture file on disk has drifted from the manifest, regenerate or check for corruption");

        var parsed = TorrentParser.Parse(torrentBytes);

        if (parsed.MetaVersion != entry.MetaVersion)
            throw new Exception(
                $"{fixtureName}: parser reported meta_version={parsed.MetaVersion}, " +
                $"libtorrent wrote {entry.MetaVersion}");

        if (parsed.PieceLength != entry.PieceLength)
            throw new Exception(
                $"{fixtureName}: parser reported piece_length={parsed.PieceLength}, " +
                $"libtorrent wrote {entry.PieceLength}");

        if (parsed.Name != entry.Name)
            throw new Exception(
                $"{fixtureName}: parser reported name=\"{parsed.Name}\", " +
                $"libtorrent wrote \"{entry.Name}\"");

        if (!string.Equals(parsed.V2InfoHash, entry.V2InfoHashSha256Hex, StringComparison.OrdinalIgnoreCase))
            throw new Exception(
                $"{fixtureName}: v2 info hash mismatch\n" +
                $"  expected (libtorrent): {entry.V2InfoHashSha256Hex}\n" +
                $"  actual   (SpawnDev):  {parsed.V2InfoHash}");

        if (expectedHasV1InfoHash)
        {
            if (string.IsNullOrEmpty(parsed.InfoHash))
                throw new Exception(
                    $"{fixtureName}: expected hybrid torrent to carry a v1 info hash, got empty string");
            if (parsed.InfoHash.Length != 40)
                throw new Exception(
                    $"{fixtureName}: v1 info hash should be 40 hex chars (SHA-1), got length {parsed.InfoHash.Length}");
        }
        else
        {
            if (!string.IsNullOrEmpty(parsed.InfoHash))
                throw new Exception(
                    $"{fixtureName}: pure-v2 torrent must NOT carry a v1 info hash, got {parsed.InfoHash}");
        }

        if (!entry.HasV2FileTree)
            throw new Exception($"{fixtureName}: manifest claims no v2 file tree — this should not be a v2 fixture");
        if (parsed.FileRoots.Length == 0)
            throw new Exception($"{fixtureName}: parser found no file roots but fixture is v2");

        // piece layers dict: libtorrent emits one entry per file whose length > piece_length
        if (entry.HasPieceLayersDict && parsed.FileRoots.Length == 1)
        {
            // single-file fixture + piece_layers present => multi-piece case
            if (parsed.PieceHashes.Length == 0)
                throw new Exception($"{fixtureName}: expected non-zero piece hashes for multi-piece v2 fixture");
        }

        // The derived algorithm property is the public entry point clients use to
        // pick SHA-1 vs SHA-256 verification — make sure it says SHA-256 for v2.
        if (parsed.PieceHashAlgorithm != "SHA-256")
            throw new Exception(
                $"{fixtureName}: v2 fixture should report PieceHashAlgorithm=SHA-256, " +
                $"got \"{parsed.PieceHashAlgorithm}\"");

        if (expectedFileCount is int expectedCount)
        {
            if (parsed.Files.Length != expectedCount)
                throw new Exception(
                    $"{fixtureName}: parser reported {parsed.Files.Length} file(s), " +
                    $"expected {expectedCount}");
        }
        else
        {
            // multi-file sentinel: > 1 file
            if (parsed.Files.Length <= 1)
                throw new Exception(
                    $"{fixtureName}: expected multi-file torrent to have >1 file, " +
                    $"parser reported {parsed.Files.Length}");
        }

        // Defense-in-depth: prove the parser captured the info-dict byte slice
        // accurately by re-hashing it ourselves and comparing to the reported
        // V2InfoHash. If the slice boundaries were off, this catches it.
        if (parsed.InfoDictBytes == null || parsed.InfoDictBytes.Length == 0)
            throw new Exception($"{fixtureName}: parser did not preserve InfoDictBytes");
        var rehashed = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(parsed.InfoDictBytes)).ToLowerInvariant();
        if (rehashed != parsed.V2InfoHash)
            throw new Exception(
                $"{fixtureName}: InfoDictBytes doesn't hash to V2InfoHash — " +
                $"parser's info-dict slice boundary is off. " +
                $"Rehashed={rehashed}, V2InfoHash={parsed.V2InfoHash}");

        await Task.CompletedTask; // keep signature parity with other async TestMethod entries
    }

    private static byte[] LoadFixture(string filename)
    {
        var asm = typeof(WebTorrentTestBase).Assembly;
        var resourceName = $"SpawnDev.WebTorrent.Demo.Shared.InteropFixtures.{filename}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new Exception(
                $"embedded fixture not found: {resourceName}. " +
                $"Known resources: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static LibtorrentManifest LoadManifest()
    {
        var asm = typeof(WebTorrentTestBase).Assembly;
        var resourceName = "SpawnDev.WebTorrent.Demo.Shared.InteropFixtures.libtorrent_reference_manifest.json";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new Exception($"embedded manifest not found: {resourceName}");
        return JsonSerializer.Deserialize<LibtorrentManifest>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        }) ?? throw new Exception("manifest deserialized to null");
    }

    private sealed class LibtorrentManifest
    {
        [JsonPropertyName("fixtures")]
        public List<LibtorrentFixture> Fixtures { get; set; } = new();
    }

    private sealed class LibtorrentFixture
    {
        [JsonPropertyName("file")]
        public string File { get; set; } = "";

        [JsonPropertyName("bytes")]
        public int Bytes { get; set; }

        [JsonPropertyName("meta_version")]
        public int MetaVersion { get; set; }

        [JsonPropertyName("piece_length")]
        public int PieceLength { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("has_v1_pieces")]
        public bool HasV1Pieces { get; set; }

        [JsonPropertyName("has_v2_file_tree")]
        public bool HasV2FileTree { get; set; }

        [JsonPropertyName("has_piece_layers_dict")]
        public bool HasPieceLayersDict { get; set; }

        [JsonPropertyName("v2_info_hash_sha256_hex")]
        public string V2InfoHashSha256Hex { get; set; } = "";
    }
}
