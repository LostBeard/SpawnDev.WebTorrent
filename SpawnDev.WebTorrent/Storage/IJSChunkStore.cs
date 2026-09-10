using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// A chunk store that can move whole pieces as JS <see cref="Uint8Array"/>s, without copying the bytes
/// through the .NET heap.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 WHY THIS IS AN INTERFACE AND NOT A CONCRETE TYPE CHECK. The download and read paths used to test
/// <c>_store is AsyncFSChunkStore</c> in eight places, and the ELSE branch of the download one is
/// <c>_store.PutAsync(p, pieceUa.ReadBytes())</c> - a full JS-&gt;.NET copy of every 4 MB piece. Adding a
/// second OPFS-backed store (<see cref="AsyncFSFileStore"/>) behind a concrete check would therefore have
/// silently routed a 7 GB model through the managed heap: correct, and a direct violation of the rule that
/// browser bulk data stays in JS. A capability belongs in a contract, not in a type name.
/// </para>
/// <para>
/// ⚠️ <see cref="SupportsUint8Array"/> is separate from implementing this interface because the same store
/// class runs on OPFS (where it holds) and on a native filesystem (where it does not). Check the property,
/// not the type.
/// </para>
/// </remarks>
public interface IJSChunkStore : IChunkStore
{
    /// <summary>True when this store can actually hand back and take JS typed arrays (browser/OPFS).</summary>
    bool SupportsUint8Array { get; }

    /// <summary>Store a whole piece straight from a JS buffer. The store does not take ownership.</summary>
    Task PutUint8ArrayAsync(int index, Uint8Array data, CancellationToken ct = default);

    /// <summary>Read a whole piece as a JS buffer, or null when the store does not hold it.</summary>
    /// <remarks>The caller owns the returned array and must dispose it.</remarks>
    Task<Uint8Array?> GetUint8ArrayAsync(int index, CancellationToken ct = default);

    /// <summary>Read part of a piece as a JS buffer, or null when the store cannot serve that range.</summary>
    /// <remarks>The caller owns the returned array and must dispose it.</remarks>
    Task<Uint8Array?> GetUint8ArrayAsync(int index, int offset, int length, CancellationToken ct = default);

    /// <summary>Whether the store holds this piece. Metadata only - never reads the bytes.</summary>
    Task<bool> PieceExistsAsync(int index, CancellationToken ct = default);

    /// <summary>
    /// Why a piece is or is not readable, for an error message. Metadata only.
    /// </summary>
    /// <remarks>
    /// ⚠️ A "shouldn't happen" throw that cannot say what it found is a dead end for whoever hits it. This
    /// turns "data not in store" into the actual state.
    /// </remarks>
    Task<string> DescribePieceAsync(int index, CancellationToken ct = default);
}
