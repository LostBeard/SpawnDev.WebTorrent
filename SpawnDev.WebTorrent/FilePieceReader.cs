namespace SpawnDev.WebTorrent;

/// <summary>
/// Reads a list of files from disk as one concatenated stream, a piece at a time (the way a multi-file v1 torrent sees its content), into ONE reused buffer: the whole content is never in
/// memory. The buffer returned by <see cref="ReadNextAsync"/> is only valid until the next call. Fails loudly if a file is shorter or longer than it was when its length was taken (it
/// changed while being read), because a torrent made from a file that moved under it is corrupt.
/// </summary>
internal sealed class FilePieceReader : IAsyncDisposable
{
    private readonly string[] _paths;
    private readonly long[] _lengths;
    private readonly byte[] _buffer;
    private int _file;
    private FileStream? _stream;
    private long _remainingInFile;

    public FilePieceReader(string[] paths, long[] lengths, int pieceLength)
    {
        _paths = paths; _lengths = lengths; _buffer = new byte[pieceLength];
        _file = -1;
    }

    /// <summary>The bytes of the current piece (valid until the next <see cref="ReadNextAsync"/>).</summary>
    public ReadOnlyMemory<byte> Piece => _buffer.AsMemory(0, _filled);
    private int _filled;

    /// <summary>Reads the next piece into the buffer (the last one may be short) and returns its length, or 0 when every file has been read.</summary>
    public async ValueTask<int> ReadNextAsync(CancellationToken ct)
    {
        int filled = 0;
        while (filled < _buffer.Length)
        {
            if (_stream == null || _remainingInFile == 0)
            {
                if (!await OpenNextAsync().ConfigureAwait(false)) break;
                if (_remainingInFile == 0) continue; // an empty file contributes nothing
            }
            int want = (int)Math.Min(_buffer.Length - filled, _remainingInFile);
            int got = await _stream!.ReadAsync(_buffer.AsMemory(filled, want), ct).ConfigureAwait(false);
            if (got == 0) throw new InvalidDataException($"'{_paths[_file]}' is shorter than when it was measured: it changed while being read");
            filled += got;
            _remainingInFile -= got;
            if (_remainingInFile == 0 && _stream.ReadByte() >= 0) throw new InvalidDataException($"'{_paths[_file]}' is longer than when it was measured: it changed while being read");
        }
        _filled = filled;
        return filled;
    }

    private async ValueTask<bool> OpenNextAsync()
    {
        if (_stream != null) { await _stream.DisposeAsync().ConfigureAwait(false); _stream = null; }
        if (++_file >= _paths.Length) return false;
        _stream = new FileStream(_paths[_file], FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _remainingInFile = _lengths[_file];
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream != null) { await _stream.DisposeAsync().ConfigureAwait(false); _stream = null; }
    }
}
