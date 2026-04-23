using System.Buffers.Binary;

namespace SpawnDev.WebTorrent;

/// <summary>
/// BEP 52 peer-protocol extension messages (types 21 / 22 / 23) for v2 Merkle-proof
/// exchange between BitTorrent v2 peers. These are CORE peer-wire message types reserved
/// by BEP 52, not BEP 10 extensions - a v2-capable peer understands them natively on the
/// wire.
///
/// Phase 2c step 1 (this commit): clean record-type definitions + encode/decode codecs
/// with known-byte-sequence test coverage. No wire dispatch or state-machine integration
/// yet - those come in step 2, which plugs these codecs into <c>Wire.cs</c> message
/// handling and <c>Torrent.Download.cs</c> piece verification so v2 peers can actually
/// request and verify Merkle proofs during active transfer.
///
/// Wire format (BEP 52 §"Protocol extension"):
///
///   hash_request (msg id 21):
///     pieces_root   : 32 bytes (file's v2 pieces root)
///     base_layer    : u32 big-endian (tree layer at which the hash range is rooted)
///     index         : u32 big-endian (starting index within base_layer)
///     length        : u32 big-endian (number of hashes requested at base_layer)
///     proof_layers  : u32 big-endian (additional sibling hashes up toward pieces_root)
///   Total header: 48 bytes; no trailing payload.
///
///   hashes (msg id 22):
///     same 48-byte header as hash_request, followed by
///     (length + proof_layers) x 32-byte SHA-256 hashes, concatenated.
///
///   hash_reject (msg id 23):
///     same 48-byte header as hash_request; no trailing payload.
/// </summary>
public static class Bep52WireMessages
{
    /// <summary>BEP 52 peer-protocol message id for <c>hash_request</c>.</summary>
    public const byte MessageIdHashRequest = 21;

    /// <summary>BEP 52 peer-protocol message id for <c>hashes</c>.</summary>
    public const byte MessageIdHashes = 22;

    /// <summary>BEP 52 peer-protocol message id for <c>hash_reject</c>.</summary>
    public const byte MessageIdHashReject = 23;

    /// <summary>Size of the common hash_request / hashes / hash_reject header in bytes.</summary>
    public const int HeaderSize = 32 + 4 + 4 + 4 + 4;

    /// <summary>SHA-256 digest size (same as <see cref="MerkleHasher.HashSize"/>).</summary>
    public const int HashSize = 32;

    /// <summary>Typed BEP 52 hash_request message.</summary>
    public readonly record struct HashRequest(byte[] PiecesRoot, uint BaseLayer, uint Index, uint Length, uint ProofLayers);

    /// <summary>Typed BEP 52 hashes message: request header plus the requested sibling hashes + proof hashes.</summary>
    public readonly record struct Hashes(byte[] PiecesRoot, uint BaseLayer, uint Index, uint Length, uint ProofLayers, byte[][] HashList);

    /// <summary>Typed BEP 52 hash_reject message. Same shape as hash_request.</summary>
    public readonly record struct HashReject(byte[] PiecesRoot, uint BaseLayer, uint Index, uint Length, uint ProofLayers);

    /// <summary>Encode a <see cref="HashRequest"/> to its 48-byte wire payload.</summary>
    public static byte[] Encode(HashRequest msg)
    {
        ValidatePiecesRoot(msg.PiecesRoot);
        var buf = new byte[HeaderSize];
        WriteHeader(buf, 0, msg.PiecesRoot, msg.BaseLayer, msg.Index, msg.Length, msg.ProofLayers);
        return buf;
    }

    /// <summary>
    /// Encode a <see cref="Hashes"/> message to its wire payload: 48-byte header followed
    /// by the concatenated 32-byte hashes. The total hash count MUST equal
    /// <c>Length + ProofLayers</c> per BEP 52; otherwise an <see cref="ArgumentException"/>
    /// is thrown so malformed responses never go on the wire.
    /// </summary>
    public static byte[] Encode(Hashes msg)
    {
        ValidatePiecesRoot(msg.PiecesRoot);
        long expectedCount = (long)msg.Length + msg.ProofLayers;
        if (msg.HashList == null || msg.HashList.Length != expectedCount)
            throw new ArgumentException(
                $"Hashes message HashList count ({msg.HashList?.Length ?? 0}) must equal Length + ProofLayers ({expectedCount}).",
                nameof(msg));
        foreach (var h in msg.HashList)
        {
            if (h == null || h.Length != HashSize)
                throw new ArgumentException($"Each hash must be exactly {HashSize} bytes.", nameof(msg));
        }

        var buf = new byte[HeaderSize + msg.HashList.Length * HashSize];
        WriteHeader(buf, 0, msg.PiecesRoot, msg.BaseLayer, msg.Index, msg.Length, msg.ProofLayers);
        int offset = HeaderSize;
        foreach (var h in msg.HashList)
        {
            Buffer.BlockCopy(h, 0, buf, offset, HashSize);
            offset += HashSize;
        }
        return buf;
    }

    /// <summary>Encode a <see cref="HashReject"/> to its 48-byte wire payload.</summary>
    public static byte[] Encode(HashReject msg)
    {
        ValidatePiecesRoot(msg.PiecesRoot);
        var buf = new byte[HeaderSize];
        WriteHeader(buf, 0, msg.PiecesRoot, msg.BaseLayer, msg.Index, msg.Length, msg.ProofLayers);
        return buf;
    }

    /// <summary>Decode a wire payload into a <see cref="HashRequest"/>.</summary>
    public static HashRequest DecodeHashRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HeaderSize)
            throw new ArgumentException($"hash_request payload must be exactly {HeaderSize} bytes; got {payload.Length}.", nameof(payload));
        var (piecesRoot, baseLayer, index, length, proofLayers) = ReadHeader(payload);
        return new HashRequest(piecesRoot, baseLayer, index, length, proofLayers);
    }

    /// <summary>Decode a wire payload into a <see cref="Hashes"/> message.</summary>
    public static Hashes DecodeHashes(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
            throw new ArgumentException($"hashes payload must be at least {HeaderSize} bytes; got {payload.Length}.", nameof(payload));
        var (piecesRoot, baseLayer, index, length, proofLayers) = ReadHeader(payload);

        long expectedCount = (long)length + proofLayers;
        long expectedBytes = HeaderSize + expectedCount * HashSize;
        if (payload.Length != expectedBytes)
            throw new ArgumentException(
                $"hashes payload length {payload.Length} does not match expected {expectedBytes} (header + (Length+ProofLayers)*{HashSize}).",
                nameof(payload));

        var hashes = new byte[expectedCount][];
        for (int i = 0; i < expectedCount; i++)
        {
            hashes[i] = payload.Slice(HeaderSize + i * HashSize, HashSize).ToArray();
        }
        return new Hashes(piecesRoot, baseLayer, index, length, proofLayers, hashes);
    }

    /// <summary>Decode a wire payload into a <see cref="HashReject"/>.</summary>
    public static HashReject DecodeHashReject(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HeaderSize)
            throw new ArgumentException($"hash_reject payload must be exactly {HeaderSize} bytes; got {payload.Length}.", nameof(payload));
        var (piecesRoot, baseLayer, index, length, proofLayers) = ReadHeader(payload);
        return new HashReject(piecesRoot, baseLayer, index, length, proofLayers);
    }

    private static void WriteHeader(byte[] buf, int offset, byte[] piecesRoot, uint baseLayer, uint index, uint length, uint proofLayers)
    {
        Buffer.BlockCopy(piecesRoot, 0, buf, offset, HashSize);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 32, 4), baseLayer);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 36, 4), index);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 40, 4), length);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 44, 4), proofLayers);
    }

    private static (byte[] PiecesRoot, uint BaseLayer, uint Index, uint Length, uint ProofLayers) ReadHeader(ReadOnlySpan<byte> payload)
    {
        var piecesRoot = payload.Slice(0, HashSize).ToArray();
        uint baseLayer = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(32, 4));
        uint index = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(36, 4));
        uint length = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(40, 4));
        uint proofLayers = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(44, 4));
        return (piecesRoot, baseLayer, index, length, proofLayers);
    }

    private static void ValidatePiecesRoot(byte[] piecesRoot)
    {
        if (piecesRoot == null || piecesRoot.Length != HashSize)
            throw new ArgumentException($"PiecesRoot must be exactly {HashSize} bytes.", nameof(piecesRoot));
    }
}
