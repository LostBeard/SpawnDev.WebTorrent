using SpawnDev.SpawnJS.Toolbox;
using Uint8Array = SpawnDev.SpawnJS.JSObjects.Uint8Array;

namespace SpawnDev.WebTorrent;

/// <summary>
/// A seekable .NET Stream backed by a torrent file. Pieces download on demand
/// as the stream is read. Works on both desktop and browser.
///
/// Implements <see cref="IJSReadStream"/> so a browser consumer can read piece data while it stays in JS
/// (a <see cref="Uint8Array"/>) - e.g. streaming model weights straight into a GPU buffer via
/// <c>IBrowserMemoryBuffer.CopyFromJS</c> without ever copying the bytes into the .NET heap.
///
/// Usage:
///   var stream = file.CreateReadStream();
///   var buffer = new byte[4096];
///   var bytesRead = await stream.ReadAsync(buffer);
///   stream.Position = 1000000; // seek
///   bytesRead = await stream.ReadAsync(buffer);
/// </summary>
public class TorrentReadStream : Stream, IJSReadStream
{
    private readonly TorrentFileInfo _file;
    private long _position;
    private readonly long _streamStart;
    private readonly long _streamEnd; // -1 = full file

    public TorrentReadStream(TorrentFileInfo file, long startPosition = 0)
    {
        _file = file;
        _position = startPosition;
        _streamStart = startPosition;
        _streamEnd = -1;
    }

    /// <summary>Create a stream for a specific byte range of the file.</summary>
    public TorrentReadStream(TorrentFileInfo file, long start, long end)
    {
        _file = file;
        _position = start;
        _streamStart = start;
        _streamEnd = end;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _streamEnd >= 0 ? _streamEnd - _streamStart + 1 : _file.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _file.Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Sync Read deadlocks in Blazor WASM (single-threaded). Use ReadAsync.
        if (OperatingSystem.IsBrowser())
            throw new NotSupportedException("Synchronous Read is not supported in Blazor WASM. Use ReadAsync.");
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var endPos = _streamEnd >= 0 ? Math.Min(_streamEnd + 1, _file.Length) : _file.Length;
        if (_position >= endPos) return 0;

        var toRead = (int)Math.Min(count, endPos - _position);
        var data = await _file.ReadAsync(_position, toRead, cancellationToken);
        Array.Copy(data, 0, buffer, offset, data.Length);
        _position += data.Length;
        return data.Length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var endPos = _streamEnd >= 0 ? Math.Min(_streamEnd + 1, _file.Length) : _file.Length;
        if (_position >= endPos) return 0;

        var toRead = (int)Math.Min(buffer.Length, endPos - _position);
        var data = await _file.ReadAsync(_position, toRead, cancellationToken);
        data.CopyTo(buffer);
        _position += data.Length;
        return data.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _file.Length + offset,
            _ => _position,
        };
        _position = Math.Clamp(_position, 0, _file.Length);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // ── IJSReadStream ──

    /// <summary>
    /// False in the browser - synchronous <see cref="Read(byte[], int, int)"/> throws in Blazor WASM
    /// (single-threaded; pieces download via async fetch). True on desktop where the sync read works.
    /// </summary>
    public bool CanReadSync => !OperatingSystem.IsBrowser();

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes from the current <see cref="Position"/> and returns them as a
    /// JS <see cref="Uint8Array"/> - the bytes stay in JS (no .NET copy), letting a consumer hand them straight
    /// to JS (e.g. a GPU buffer via <c>IBrowserMemoryBuffer.CopyFromJS</c>). Advances <see cref="Position"/>.
    /// Returns an empty <see cref="Uint8Array"/> at end of stream. Browser-only (the underlying read produces a
    /// JS object); on desktop use the <c>byte[]</c> <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>.
    /// </summary>
    public async Task<Uint8Array> ReadUint8ArrayAsync(int count, CancellationToken cancellationToken = default)
    {
        var endPos = _streamEnd >= 0 ? Math.Min(_streamEnd + 1, _file.Length) : _file.Length;
        if (_position >= endPos || count <= 0) return new Uint8Array(0);
        var toRead = (int)Math.Min(count, endPos - _position);
        var data = await _file.ReadUint8ArrayAsync(_position, toRead, cancellationToken);
        _position += data.Length;
        return data;
    }

    /// <summary>
    /// Synchronous <see cref="ReadUint8ArrayAsync"/> counterpart (the <see cref="IJSReadStream"/> member
    /// added in SpawnDev.SpawnJS 3.5.14 - without this implementation the TYPE fails to load against
    /// 3.5.14+ with a TypeLoadException in any consumer, even code that never calls it). Honors the
    /// <see cref="CanReadSync"/> contract: throws in Blazor WASM (pieces download via async fetch on the
    /// single thread - blocking would deadlock, same as sync <see cref="Read(byte[], int, int)"/>); on
    /// desktop it performs the read by blocking on the async path, exactly like the sync Read override.
    /// </summary>
    public Uint8Array ReadUint8Array(int count)
    {
        if (!CanReadSync)
            throw new NotSupportedException("Synchronous ReadUint8Array is not supported in Blazor WASM. Use ReadUint8ArrayAsync.");
        return ReadUint8ArrayAsync(count).GetAwaiter().GetResult();
    }
}
