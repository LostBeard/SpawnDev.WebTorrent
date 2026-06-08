// BEP 52 (BitTorrent v2) Merkle piece-root computation, fully in JS.
//
// WHY THIS EXISTS: the zero-copy browser web-seed download (Torrent.Download.cs ->
// VerifyPieceZeroCopyAsync) used to hash each 16 KiB leaf with a SEPARATE SubtleCrypto
// call awaited from .NET, then marshal every 32-byte leaf digest back across the
// .NET<->JS boundary to build the tree in MerkleHasher.ComputePieceRootFromLeafHashes.
// For a ~4 MB piece that is ~256 Promise->Task bridges + ~256 result copies PER PIECE,
// times ~600 pieces for a 2.5 GB model = ~150k promise bridges. The crypto work is
// native and fast; the cost is the per-leaf boundary marshaling.
//
// This module does ALL of it in one .NET->JS call per piece: hash every leaf with
// crypto.subtle, await them with a single Promise.all (zero .NET promise bridges),
// build the BEP-52 tree here, and return only the 32-byte root. The piece bytes and the
// intermediate leaf hashes never cross into .NET.
//
// Semantics are byte-identical to MerkleHasher.ComputePieceRootFromLeafHashes /
// ComputePieceLayer (verified by an equivalence test): the final partial leaf is
// zero-padded to 16 KiB before hashing, empty leaf slots in the piece are filled with
// the level-0 zero-pad hash (SHA-256 of 16 KiB of zeros), and pairs are combined with
// SHA-256 until one root remains. A single-leaf piece returns the leaf hash directly.

const LEAF_SIZE = 16384;
const HASH_SIZE = 32;

// Memoized level-0 zero-pad hash = SHA-256 of 16 KiB of zero bytes. Returns a Promise the
// first time, then the resolved Uint8Array; awaiting a non-Promise is harmless.
let _zeroLeafHash = null;
async function zeroLeafHash() {
    if (_zeroLeafHash === null) {
        const d = await crypto.subtle.digest('SHA-256', new Uint8Array(LEAF_SIZE));
        _zeroLeafHash = new Uint8Array(d);
    }
    return _zeroLeafHash;
}

// Combine an array of 32-byte Uint8Array hashes pairwise with SHA-256 until one remains.
// `level` MUST already be a power-of-two length (the caller pads to leavesPerPiece, which
// is a power of two). Mirrors MerkleHasher.ComputeRoot's pairwise reduction.
async function reducePairs(level) {
    while (level.length > 1) {
        const pairDigests = [];
        for (let i = 0; i < level.length; i += 2) {
            const pair = new Uint8Array(HASH_SIZE * 2);
            pair.set(level[i], 0);
            pair.set(level[i + 1], HASH_SIZE);
            pairDigests.push(crypto.subtle.digest('SHA-256', pair));
        }
        const results = await Promise.all(pairDigests);
        const next = new Array(results.length);
        for (let i = 0; i < results.length; i++) next[i] = new Uint8Array(results[i]);
        level = next;
    }
    return level[0];
}

/**
 * Compute ONE piece's BEP-52 Merkle root from the piece's raw bytes.
 * @param {Uint8Array} pieceData - the piece bytes (length may be < pieceLen for the file's final piece; views are fine).
 * @param {number} pieceLen - number of valid content bytes in pieceData.
 * @param {number} leavesPerPiece - pieceSize / 16 KiB; a power of two >= 1.
 * @returns {Promise<Uint8Array>} the 32-byte piece root.
 */
export async function computePieceRoot(pieceData, pieceLen, leavesPerPiece) {
    const actualLeaves = Math.ceil(pieceLen / LEAF_SIZE);

    // Hash all content leaves concurrently. The final partial leaf is zero-padded to 16 KiB.
    const leafDigests = new Array(actualLeaves);
    for (let li = 0; li < actualLeaves; li++) {
        const start = li * LEAF_SIZE;
        const len = Math.min(LEAF_SIZE, pieceLen - start);
        let input;
        if (len === LEAF_SIZE) {
            input = pieceData.subarray(start, start + len);
        } else {
            input = new Uint8Array(LEAF_SIZE);          // zero-filled tail pad
            input.set(pieceData.subarray(start, start + len), 0);
        }
        leafDigests[li] = crypto.subtle.digest('SHA-256', input);
    }
    const resolved = await Promise.all(leafDigests);

    // Bottom level: actual leaf hashes, padded out to leavesPerPiece with the level-0 zero-pad hash.
    const level = new Array(leavesPerPiece);
    for (let li = 0; li < actualLeaves; li++) level[li] = new Uint8Array(resolved[li]);
    if (actualLeaves < leavesPerPiece) {
        const zero = await zeroLeafHash();
        for (let li = actualLeaves; li < leavesPerPiece; li++) level[li] = zero;
    }

    if (leavesPerPiece === 1) return level[0];
    return await reducePairs(level);
}
