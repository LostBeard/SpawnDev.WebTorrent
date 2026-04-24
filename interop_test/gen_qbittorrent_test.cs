// Generates three .torrent files for manual qBittorrent interop:
//   - spawndev_v1.torrent   (v1-only, SHA-1 piece hashes)
//   - spawndev_v2.torrent   (pure v2, BEP 52 Merkle only)
//   - spawndev_hybrid.torrent (v1+v2 hybrid)
// plus a shared data file (`payload.bin`, 1 MiB deterministic pattern).
//
// What to check in qBittorrent after loading each:
//   1. File opens without error.
//   2. Info Hash v1 (spawndev_v1 + spawndev_hybrid): matches the SHA1 printed by this script.
//   3. Info Hash v2 (spawndev_v2 + spawndev_hybrid): matches the SHA256 printed by this script.
//   4. File size / piece count / piece length match the printed manifest.
//   5. Add payload.bin to the same folder and verify qBittorrent piece-hashes it clean
//      (Force Recheck → 100% after recheck; no pieces marked bad).
//
// Run: dotnet run gen_qbittorrent_test.cs

#:project D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent.csproj

using SpawnDev.WebTorrent;

var outDir = AppContext.BaseDirectory;
var scriptPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith("gen_qbittorrent_test.cs"));
if (scriptPath != null) outDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
else outDir = Directory.GetCurrentDirectory();

// 1 MiB deterministic payload so qBittorrent can hash-verify against our printed hashes.
var rng = new Random(0x5E3DC0DE);
var payload = new byte[1024 * 1024];
rng.NextBytes(payload);
File.WriteAllBytes(Path.Combine(outDir, "payload.bin"), payload);

var trackers = new[]
{
    "wss://tracker.openwebtorrent.com",
    "wss://hub.spawndev.com:44365/announce",
};

// v1-only: classic SHA-1 piece hashes.
{
    var (bytes, meta) = TorrentCreator.CreateFromBytes("payload.bin", payload,
        new TorrentCreatorOptions
        {
            PieceLength = 65536,
            Trackers = trackers,
            HashAlgorithm = "SHA-1",
            Comment = "SpawnDev.WebTorrent v1 interop test",
        });
    File.WriteAllBytes(Path.Combine(outDir, "spawndev_v1.torrent"), bytes);
    Console.WriteLine($"spawndev_v1.torrent: v1={meta.InfoHash}, pieces={meta.PieceCount}, pieceLen={meta.PieceLength}, totalLen={meta.TotalLength}");
}

// Pure v2: BEP 52 Merkle only, no v1 piece list.
{
    var (bytes, meta) = TorrentCreator.CreateFromBytes("payload.bin", payload,
        new TorrentCreatorOptions
        {
            PieceLength = 65536,
            Trackers = trackers,
            MetaVersion = 2,
            Hybrid = false,
            Comment = "SpawnDev.WebTorrent pure-v2 interop test",
        });
    File.WriteAllBytes(Path.Combine(outDir, "spawndev_v2.torrent"), bytes);
    Console.WriteLine($"spawndev_v2.torrent: v2={meta.V2InfoHash}, pieces={meta.PieceCount}, pieceLen={meta.PieceLength}, totalLen={meta.TotalLength}");
}

// Hybrid v1+v2: both in one info dict.
{
    var (bytes, meta) = TorrentCreator.CreateFromBytes("payload.bin", payload,
        new TorrentCreatorOptions
        {
            PieceLength = 65536,
            Trackers = trackers,
            MetaVersion = 2,
            Hybrid = true,
            Comment = "SpawnDev.WebTorrent hybrid v1+v2 interop test",
        });
    File.WriteAllBytes(Path.Combine(outDir, "spawndev_hybrid.torrent"), bytes);
    Console.WriteLine($"spawndev_hybrid.torrent: v1={meta.InfoHash}, v2={meta.V2InfoHash}, pieces={meta.PieceCount}, pieceLen={meta.PieceLength}, totalLen={meta.TotalLength}");
}

Console.WriteLine($"\nAll output written to: {outDir}");
Console.WriteLine("payload.bin: 1048576 bytes, deterministic (seed=0x5E3DC0DE)");
