namespace SpawnDev.WebTorrent;

/// <summary>
/// A seekable .NET Stream backed by a torrent file. Pieces download on demand
/// as the stream is read. Works on both desktop and browser.
///
/// Usage:
///   var stream = file.CreateReadStream();
///   var buffer = new byte[4096];
///   var bytesRead = await stream.ReadAsync(buffer);
///   stream.Position = 1000000; // seek
///   bytesRead = await stream.ReadAsync(buffer);
/// </summary>
public class TorrentReadStream : Stream
{
    private readonly TorrentFileInfo _file;
    private long _position;
    private readonly long _streamEnd; // -1 = full file

    public TorrentReadStream(TorrentFileInfo file, long startPosition = 0)
    {
        _file = file;
        _position = startPosition;
        _streamEnd = -1;
    }

    /// <summary>Create a stream for a specific byte range of the file.</summary>
    public TorrentReadStream(TorrentFileInfo file, long start, long end)
    {
        _file = file;
        _position = start;
        _streamEnd = end;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _streamEnd >= 0 ? _streamEnd + 1 : _file.Length;

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
}
