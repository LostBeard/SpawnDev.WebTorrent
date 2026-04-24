// Minimal JS WebTorrent seeder for SpawnDev.WebTorrent interop testing.
//
// Starts a WebTorrent instance in Node.js with WebRTC support via @roamhq/wrtc,
// seeds a given file using a provided .torrent, announces to one or more
// WebSocket trackers (so our C# WebRTC-based peer can discover it), and keeps
// the process alive until killed.
//
// Usage:
//   node seeder.js <torrent-file> <payload-file-or-dir>
//
// Emits to stdout:
//   READY infohash=<hex> magnet=<magnet-uri>
//   PROGRESS uploaded=<N> peers=<N>
//   PEER-CONNECT addr=<remote>
// Stderr is human-readable error output only.

import WebTorrent from 'webtorrent';
import wrtc from '@roamhq/wrtc';
import fs from 'node:fs';
import path from 'node:path';

const [, , torrentPath, payloadPath] = process.argv;
if (!torrentPath || !payloadPath) {
    console.error('Usage: node seeder.js <torrent-file> <payload-file-or-dir>');
    process.exit(2);
}

if (!fs.existsSync(torrentPath)) {
    console.error(`Torrent file not found: ${torrentPath}`);
    process.exit(2);
}
if (!fs.existsSync(payloadPath)) {
    console.error(`Payload not found: ${payloadPath}`);
    process.exit(2);
}

// WebTorrent needs a WebRTC implementation for peer connections in Node.js.
// The top-level `wrtc` option gives simple-peer WebRTC support for peer
// connections. The nested `tracker.wrtc` gives bittorrent-tracker's
// WebSocket-tracker client WebRTC offer generation support - WITHOUT that,
// the WebSocketTracker silently skips ws:// / wss:// trackers (its internal
// webrtcSupport guard gates announce itself, not just peer connect).
const client = new WebTorrent({ wrtc, tracker: { wrtc } });

client.on('error', (err) => {
    console.error(`[WebTorrent error] ${err?.message ?? err}`);
});

// `path` controls where WebTorrent looks for the payload. Point it at the
// DIRECTORY containing the payload so WebTorrent's name-resolve picks the
// existing file on disk instead of trying to re-download.
const torrentBytes = fs.readFileSync(torrentPath);
const payloadDir = fs.statSync(payloadPath).isDirectory() ? payloadPath : path.dirname(payloadPath);

// Add + force verify. WebTorrent's `seed()` would re-hash from disk, but
// add-with-path + skipVerify: false gets us the same piece-verified state
// without reading the torrent's name->disk mapping wrong.
const torrent = client.add(torrentBytes, { path: payloadDir });

torrent.on('error', (err) => {
    console.error(`[Torrent error] ${err?.message ?? err}`);
});

torrent.on('ready', () => {
    console.log(`READY infohash=${torrent.infoHash} magnet=${torrent.magnetURI}`);
    console.log(`  name=${torrent.name} pieces=${torrent.pieces.length} length=${torrent.length}`);
    console.log(`  trackers=${torrent.announce.length}`);
    for (const t of torrent.announce) console.log(`    - ${t}`);
});

torrent.on('wire', (wire, addr) => {
    console.log(`PEER-CONNECT addr=${addr ?? 'unknown'}`);
});

torrent.on('trackerAnnounce', () => {
    console.log(`TRACKER-ANNOUNCE`);
});
torrent.on('warning', (w) => {
    console.log(`WARNING ${w?.message ?? w}`);
});
client.on('warning', (w) => {
    console.log(`CLIENT-WARNING ${w?.message ?? w}`);
});

let lastUploaded = 0;
const reportInterval = setInterval(() => {
    if (!torrent.destroyed && torrent.uploaded !== lastUploaded) {
        console.log(`PROGRESS uploaded=${torrent.uploaded} peers=${torrent.numPeers}`);
        lastUploaded = torrent.uploaded;
    }
}, 1000);

function shutdown(signal) {
    console.log(`Shutting down (${signal}) uploaded=${torrent?.uploaded ?? 0}`);
    clearInterval(reportInterval);
    client.destroy(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000).unref();
}

process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));
// Windows signals.
process.on('SIGBREAK', () => shutdown('SIGBREAK'));
