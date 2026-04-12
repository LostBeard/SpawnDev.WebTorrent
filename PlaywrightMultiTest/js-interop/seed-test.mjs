// JS WebTorrent seeder for interop testing with SpawnDev.WebTorrent C# client
// Seeds deterministic data via WebRTC through hub.spawndev.com tracker
// Outputs magnet URI on stdout for the C# test to consume

import WebTorrent from 'webtorrent'

const TRACKER = 'wss://hub.spawndev.com:44365/announce'
const DATA_SIZE = 32768
const PIECE_LENGTH = 16384
const TIMEOUT_MS = 120_000

// Generate deterministic data (must match C# test)
const data = Buffer.alloc(DATA_SIZE)
for (let i = 0; i < DATA_SIZE; i++) data[i] = ((i * 7 + 13) % 256)

const client = new WebTorrent()

console.error('[JS] Creating WebTorrent client...')
console.error(`[JS] PeerId: ${client.peerId}`)

client.on('error', (err) => console.error(`[JS] Client error: ${err.message}`))
client.on('warning', (err) => console.error(`[JS] Client warning: ${err.message}`))

// Seed the data
client.seed(data, {
  name: 'interop-test.bin',
  pieceLength: PIECE_LENGTH,
  announce: [TRACKER],
}, (torrent) => {
  console.error(`[JS] Seeding: ${torrent.name}`)
  console.error(`[JS] InfoHash: ${torrent.infoHash}`)
  console.error(`[JS] MagnetURI: ${torrent.magnetURI}`)
  console.error(`[JS] Pieces: ${torrent.pieces.length}`)
  console.error(`[JS] Tracker: ${TRACKER}`)

  // Output magnet URI on stdout for C# to read
  console.log(torrent.magnetURI)

  // Log peer connections
  torrent.on('wire', (wire) => {
    console.error(`[JS] Wire connected: ${wire.peerId?.toString('hex')?.substring(0, 16)}... type=${wire.type}`)
    wire.on('interested', () => console.error(`[JS] Peer interested`))
    wire.on('request', (index, offset, length) => console.error(`[JS] Piece request: index=${index} offset=${offset} length=${length}`))
    wire.on('upload', (bytes) => console.error(`[JS] Uploaded ${bytes} bytes`))
  })

  torrent.on('upload', (bytes) => console.error(`[JS] Total uploaded: ${torrent.uploaded} bytes`))

  // Log tracker events
  torrent.on('trackerAnnounce', () => console.error(`[JS] Tracker announce`))
  torrent.on('trackerWarning', (err) => console.error(`[JS] Tracker warning: ${err.message}`))
  torrent.on('trackerError', (err) => console.error(`[JS] Tracker error: ${err.message}`))

  // Auto-exit after timeout
  setTimeout(() => {
    console.error(`[JS] Timeout - destroying client`)
    client.destroy(() => process.exit(0))
  }, TIMEOUT_MS)
})
