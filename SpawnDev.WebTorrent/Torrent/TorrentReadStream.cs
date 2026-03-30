namespace SpawnDev.WebTorrent.Torrent;

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
    private readonly TorrentFileStream _file;
    private long _position;

    public TorrentReadStream(TorrentFileStream file, long startPosition = 0)
    {
        _file = file;
        _position = startPosition;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _file.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _file.Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Sync Read is not supported — Blazor WASM is single-threaded and
        // .GetAwaiter().GetResult() will deadlock. Use ReadAsync instead.
        if (OperatingSystem.IsBrowser())
            throw new NotSupportedException("Synchronous Read is not supported in Blazor WASM. Use ReadAsync.");
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_position >= _file.Length) return 0;

        var toRead = (int)Math.Min(count, _file.Length - _position);
        var data = await _file.ReadAsync(_position, toRead, cancellationToken);
        Array.Copy(data, 0, buffer, offset, data.Length);
        _position += data.Length;
        return data.Length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _file.Length) return 0;

        var toRead = (int)Math.Min(buffer.Length, _file.Length - _position);
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
