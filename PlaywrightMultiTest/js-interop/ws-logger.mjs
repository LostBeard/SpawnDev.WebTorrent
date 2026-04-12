// WebSocket MITM logger - logs all tracker messages from a JS WebTorrent client
// Use to compare message format against our C# client
// Usage: node ws-logger.mjs [magnet_uri]

import WebTorrent from 'webtorrent'

const DEFAULT_MAGNET = 'magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F'

const magnet = process.argv[2] || DEFAULT_MAGNET

// Monkey-patch WebSocket to log all messages
const OrigWS = globalThis.WebSocket || (await import('ws')).default
class LoggingWebSocket extends OrigWS {
  constructor(url, protocols) {
    super(url, protocols)
    this._url = url
    console.error(`[WS] CONNECT: ${url}`)

    this.addEventListener('open', () => console.error(`[WS] OPEN: ${url}`))
    this.addEventListener('close', (e) => console.error(`[WS] CLOSE: ${url} code=${e.code}`))
    this.addEventListener('error', (e) => console.error(`[WS] ERROR: ${url} ${e.message || ''}`))
    this.addEventListener('message', (e) => {
      const data = typeof e.data === 'string' ? e.data : e.data.toString()
      try {
        const msg = JSON.parse(data)
        const infoHashHex = msg.info_hash ? Buffer.from(msg.info_hash, 'binary').toString('hex') : null
        const peerIdHex = msg.peer_id ? Buffer.from(msg.peer_id, 'binary').toString('hex').substring(0, 16) : null
        console.error(`[WS] RECV ${url}: action=${msg.action} info_hash=${infoHashHex?.substring(0,8)}... peer_id=${peerIdHex}... ` +
          `${msg.complete !== undefined ? `complete=${msg.complete}` : ''} ` +
          `${msg.incomplete !== undefined ? `incomplete=${msg.incomplete}` : ''} ` +
          `${msg.offer ? 'HAS_OFFER' : ''} ${msg.answer ? 'HAS_ANSWER' : ''} ` +
          `${msg.offers ? `offers=${msg.offers.length}` : ''}`)
      } catch {
        console.error(`[WS] RECV ${url}: (raw) ${data.substring(0, 200)}`)
      }
    })

    const origSend = this.send.bind(this)
    this.send = (data) => {
      const str = typeof data === 'string' ? data : data.toString()
      try {
        const msg = JSON.parse(str)
        const infoHashHex = msg.info_hash ? Buffer.from(msg.info_hash, 'binary').toString('hex') : null
        console.error(`[WS] SEND ${url}: action=${msg.action} info_hash=${infoHashHex?.substring(0,8)}... ` +
          `${msg.event ? `event=${msg.event}` : ''} ` +
          `${msg.numwant ? `numwant=${msg.numwant}` : ''} ` +
          `${msg.offers ? `offers=${msg.offers.length}` : ''} ` +
          `${msg.answer ? 'HAS_ANSWER' : ''}`)
        // Log the raw JSON for byte comparison
        console.log(`SEND|${url}|${str}`)
      } catch {
        console.error(`[WS] SEND ${url}: (raw) ${str.substring(0, 200)}`)
      }
      origSend(data)
    }
  }
}
globalThis.WebSocket = LoggingWebSocket

const client = new WebTorrent()
console.error(`[JS] PeerId: ${client.peerId}`)
console.error(`[JS] Adding: ${magnet.substring(0, 80)}...`)

const torrent = client.add(magnet)

torrent.on('metadata', () => {
  console.error(`[JS] Metadata: ${torrent.name}, ${torrent.pieces.length} pieces`)
})

torrent.on('done', () => {
  console.error(`[JS] Done! Downloaded ${torrent.downloaded} bytes`)
})

torrent.on('wire', (wire) => {
  console.error(`[JS] Wire: ${wire.peerId?.toString('hex')?.substring(0, 16)}... type=${wire.type}`)
})

// Auto-exit after 60s
setTimeout(() => {
  console.error(`[JS] Timeout, exiting`)
  client.destroy(() => process.exit(0))
}, 60_000)
