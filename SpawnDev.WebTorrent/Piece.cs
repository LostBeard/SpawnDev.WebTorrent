namespace SpawnDev.WebTorrent;

/// <summary>
/// Tracks block-level download state for a single torrent piece.
/// Direct 1:1 port of torrent-piece/index.js from JS WebTorrent.
/// </summary>
public class Piece
{
    /// <summary>Standard BitTorrent block size: 16 KB (1 &lt;&lt; 14).</summary>
    public const int BlockLength = 1 << 14; // 16384

    /// <summary>Total piece size in bytes.</summary>
    public int Length { get; }

    /// <summary>Bytes still missing (decreases as blocks arrive).</summary>
    public int Missing { get; private set; }

    /// <summary>Peer sources that contributed blocks to this piece.</summary>
    public List<string>? Sources { get; private set; }

    private readonly int _chunks;
    private readonly int _remainder;
    private int _buffered;
    private byte[]?[]? _buffer;
    private List<int>? _cancellations;
    private int _reservations;
    private bool _flushed;

    // Piece is a 1:1 port of JS torrent-piece (single-threaded event loop). The C# client drives
    // download from MULTIPLE threads concurrently — the download loop calls Reserve()/ReserveRemaining()
    // while a wire-arrival callback on another thread calls Set()/Flush() (which null _buffer/_cancellations).
    // That raced: Init() returns true, then _cancellations gets nulled by a concurrent Flush() before the
    // caller dereferences it → NullReferenceException (hit loading SD-Turbo's large multi-piece torrent).
    // Serialize ALL state mutators on this gate so the Piece state machine is atomic. Monitor is
    // re-entrant (the nested Init() re-acquire is safe) and a no-op on single-threaded WASM.
    private readonly object _gate = new();

    public Piece(int length)
    {
        Length = length;
        Missing = length;
        Sources = null;

        _chunks = (int)Math.Ceiling((double)length / BlockLength);
        _remainder = (length % BlockLength) == 0 ? BlockLength : (length % BlockLength);
        _buffered = 0;
        _buffer = null;
        _cancellations = null;
        _reservations = 0;
        _flushed = false;
    }

    /// <summary>Get the byte length of block i.</summary>
    public int ChunkLength(int i)
        => i == _chunks - 1 ? _remainder : BlockLength;

    /// <summary>Get the remaining bytes from block i to end of piece.</summary>
    public int ChunkLengthRemaining(int i)
        => Length - (i * BlockLength);

    /// <summary>Get the byte offset of block i within the piece.</summary>
    public int ChunkOffset(int i)
        => i * BlockLength;

    /// <summary>
    /// Reserve a single block for download. Returns block index, or -1 if none available.
    /// Cancelled blocks are re-reserved first (stack-based — LIFO).
    /// </summary>
    public int Reserve()
    {
        lock (_gate)
        {
            if (!Init()) return -1;
            if (_cancellations!.Count > 0)
            {
                var idx = _cancellations[^1];
                _cancellations.RemoveAt(_cancellations.Count - 1);
                return idx;
            }
            if (_reservations < _chunks) return _reservations++;
            return -1;
        }
    }

    /// <summary>
    /// Reserve ALL remaining blocks (used by web seeds). Returns the lowest block index, or -1.
    /// </summary>
    public int ReserveRemaining()
    {
        lock (_gate)
        {
            if (!Init()) return -1;
            if (_cancellations!.Count > 0 || _reservations < _chunks)
            {
                int min = _reservations;
                while (_cancellations.Count > 0)
                {
                    min = Math.Min(min, _cancellations[^1]);
                    _cancellations.RemoveAt(_cancellations.Count - 1);
                }
                _reservations = _chunks;
                return min;
            }
            return -1;
        }
    }

    /// <summary>Cancel a block reservation (block will be re-requested).</summary>
    public void Cancel(int i)
    {
        lock (_gate)
        {
            if (!Init()) return;
            _cancellations!.Add(i);
        }
    }

    /// <summary>Cancel all reservations from block i onward.</summary>
    public void CancelRemaining(int i)
    {
        lock (_gate)
        {
            if (!Init()) return;
            _reservations = i;
        }
    }

    /// <summary>Get block data at index i, or null.</summary>
    public byte[]? Get(int i)
    {
        lock (_gate)
        {
            if (!Init()) return null;
            return _buffer![i];
        }
    }

    /// <summary>
    /// Set block data. Returns true if piece is now complete (all blocks received).
    /// Handles multi-block data (data larger than BlockLength is split).
    /// Duplicate blocks are silently ignored.
    /// </summary>
    public bool Set(int i, byte[] data, string source)
    {
        lock (_gate)
        {
            if (!Init()) return false;
            int len = data.Length;
            int blocks = (int)Math.Ceiling((double)len / BlockLength);
            for (int j = 0; j < blocks; j++)
            {
                if (_buffer![i + j] == null)
                {
                    int offset = j * BlockLength;
                    int end = Math.Min(offset + BlockLength, len);
                    byte[] splitData = new byte[end - offset];
                    Array.Copy(data, offset, splitData, 0, splitData.Length);
                    _buffered++;
                    _buffer[i + j] = splitData;
                    Missing -= splitData.Length;
                    if (!Sources!.Contains(source))
                    {
                        Sources.Add(source);
                    }
                }
            }
            return _buffered == _chunks;
        }
    }

    /// <summary>
    /// Flush: concatenate all blocks into a single byte array and clear buffers.
    /// Returns null if not all blocks are received.
    /// </summary>
    public byte[]? Flush()
    {
        lock (_gate)
        {
            if (_buffer == null || _chunks != _buffered) return null;
            var result = new byte[Length];
            int pos = 0;
            for (int i = 0; i < _chunks; i++)
            {
                var block = _buffer[i]!;
                Array.Copy(block, 0, result, pos, block.Length);
                pos += block.Length;
            }
            _buffer = null;
            _cancellations = null;
            Sources = null;
            _flushed = true;
            return result;
        }
    }

    /// <summary>
    /// Initialize buffers on first access. Returns false if already flushed.
    /// </summary>
    public bool Init()
    {
        lock (_gate)
        {
            if (_flushed) return false;
            if (_buffer != null) return true;
            _buffer = new byte[_chunks][];
            _cancellations = new List<int>();
            Sources = new List<string>();
            return true;
        }
    }
}
