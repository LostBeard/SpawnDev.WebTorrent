// WebSocket MITM proxy for BitTorrent tracker protocol analysis
// Sits between client and tracker, logs all messages in both directions
// Usage: node tracker-mitm.mjs [listen_port] [target_tracker_url]
// Example: node tracker-mitm.mjs 9999 wss://tracker.openwebtorrent.com

import { WebSocketServer, WebSocket } from 'ws'
import http from 'http'

const LISTEN_PORT = parseInt(process.argv[2] || '9999')
const TARGET_URL = process.argv[3] || 'wss://tracker.openwebtorrent.com'

const server = http.createServer()
const wss = new WebSocketServer({ server })

console.log(`[MITM] Listening on ws://localhost:${LISTEN_PORT}`)
console.log(`[MITM] Proxying to ${TARGET_URL}`)

let connId = 0

wss.on('connection', (clientWs, req) => {
  const id = ++connId
  console.log(`[MITM] Client #${id} connected from ${req.socket.remoteAddress}`)

  const targetWs = new WebSocket(TARGET_URL)

  targetWs.on('open', () => {
    console.log(`[MITM] #${id} -> Target connected`)
  })

  // Client -> Target (with logging)
  clientWs.on('message', (data) => {
    const str = data.toString()
    try {
      const msg = JSON.parse(str)
      const ih = msg.info_hash ? Buffer.from(msg.info_hash, 'binary').toString('hex').substring(0, 8) : null
      const pid = msg.peer_id ? Buffer.from(msg.peer_id, 'binary').toString('hex').substring(0, 16) : null

      // Log summary
      console.log(`[C->T] #${id} action=${msg.action} ih=${ih}... pid=${pid}... ` +
        `${msg.event ? 'event=' + msg.event : ''} ` +
        `${msg.numwant ? 'numwant=' + msg.numwant : ''} ` +
        `${msg.offers ? 'offers=' + msg.offers.length : ''} ` +
        `${msg.answer ? 'HAS_ANSWER' : ''} ` +
        `${msg.offer ? 'HAS_OFFER' : ''}`)

      // Log which keys are present (important for omit-when-null compliance)
      console.log(`[C->T] #${id} keys: [${Object.keys(msg).join(', ')}]`)

      // Log raw for byte comparison (first 500 chars)
      console.log(`[C->T] #${id} raw: ${str.substring(0, 500)}`)
    } catch {
      console.log(`[C->T] #${id} (binary ${data.length} bytes)`)
    }

    if (targetWs.readyState === WebSocket.OPEN) {
      targetWs.send(data)
    }
  })

  // Target -> Client (with logging)
  targetWs.on('message', (data) => {
    const str = data.toString()
    try {
      const msg = JSON.parse(str)
      const ih = msg.info_hash ? Buffer.from(msg.info_hash, 'binary').toString('hex').substring(0, 8) : null

      console.log(`[T->C] #${id} action=${msg.action} ih=${ih}... ` +
        `${msg.complete !== undefined ? 'complete=' + msg.complete : ''} ` +
        `${msg.incomplete !== undefined ? 'incomplete=' + msg.incomplete : ''} ` +
        `${msg.offer ? 'HAS_OFFER' : ''} ${msg.answer ? 'HAS_ANSWER' : ''} ` +
        `${msg.peer_id ? 'pid=' + Buffer.from(msg.peer_id, 'binary').toString('hex').substring(0, 16) + '...' : ''}`)

      console.log(`[T->C] #${id} keys: [${Object.keys(msg).join(', ')}]`)
    } catch {
      console.log(`[T->C] #${id} (binary ${data.length} bytes)`)
    }

    if (clientWs.readyState === WebSocket.OPEN) {
      clientWs.send(data)
    }
  })

  targetWs.on('error', (err) => console.log(`[MITM] #${id} target error: ${err.message}`))
  targetWs.on('close', () => {
    console.log(`[MITM] #${id} target closed`)
    clientWs.close()
  })

  clientWs.on('close', () => {
    console.log(`[MITM] #${id} client closed`)
    targetWs.close()
  })

  clientWs.on('error', (err) => console.log(`[MITM] #${id} client error: ${err.message}`))
})

server.listen(LISTEN_PORT, () => {
  console.log(`[MITM] Ready. Point your client to ws://localhost:${LISTEN_PORT}`)
})
