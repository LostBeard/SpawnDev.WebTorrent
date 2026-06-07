namespace SpawnDev.WebTorrent;

/// <summary>
/// Tracks piece availability across all peers for rarest-first selection.
/// Direct 1:1 port of rarity-map.js from JS WebTorrent.
/// </summary>
public class RarityMap
{
    private readonly int _numPieces;
    private int[] _pieces;
    private Torrent? _torrent;
    private static readonly Random _random = new();

    public RarityMap(Torrent torrent)
    {
        _torrent = torrent;
        _numPieces = torrent.Pieces.Length;
        _pieces = new int[_numPieces];

        // Subscribe to existing wires
        foreach (var wire in torrent.Wires)
            InitWire(wire);

        // Subscribe to new wires
        torrent.OnWire += OnNewWire;

        Recalculate();
    }

    /// <summary>
    /// Get the index of the rarest piece. Returns -1 if no piece matches the filter.
    /// When multiple pieces have equal rarity, one is chosen randomly.
    /// </summary>
    public int GetRarestPiece(Func<int, bool>? filter = null)
    {
        var candidates = new List<int>();
        int min = int.MaxValue;

        // Snapshot for thread safety - _pieces can be reassigned by Recalculate on another thread
        var pieces = _pieces;
        var numPieces = Math.Min(_numPieces, pieces.Length);
        for (int i = 0; i < numPieces; i++)
        {
            if (filter != null && !filter(i)) continue;

            int availability = pieces[i];
            if (availability == min)
            {
                candidates.Add(i);
            }
            else if (availability < min)
            {
                candidates.Clear();
                candidates.Add(i);
                min = availability;
            }
        }

        if (candidates.Count > 0)
            return candidates[_random.Next(candidates.Count)];
        return -1;
    }

    /// <summary>Recalculate piece availability from all connected wires.</summary>
    public void Recalculate()
    {
        Array.Fill(_pieces, 0);
        if (_torrent == null) return;

        foreach (var wire in _torrent.Wires)
        {
            for (int i = 0; i < _numPieces && i < _pieces.Length; i++)
            {
                if (wire.PeerPieces != null && i < wire.PeerPieces.Length && wire.PeerPieces[i])
                    _pieces[i]++;
            }
        }
    }

    public void Destroy()
    {
        if (_torrent != null)
        {
            _torrent.OnWire -= OnNewWire;
            foreach (var wire in _torrent.Wires)
                CleanupWireEvents(wire);
            _torrent = null;
        }
        _pieces = Array.Empty<int>();
    }

    private void OnNewWire(Wire wire, string addr)
    {
        Recalculate();
        InitWire(wire);
    }

    private void InitWire(Wire wire)
    {
        // Snapshot _pieces locally on every access: Destroy() swaps it to Array.Empty and
        // Recalculate() reallocates it, both potentially concurrent with these wire-event
        // callbacks during teardown — reading the field twice (check then index) raced and
        // threw IndexOutOfRange on dispose. Bound by the snapshot's own length.
        wire.OnHave += (index) => { var p = _pieces; if (index >= 0 && index < p.Length) p[index]++; };
        wire.OnBitfield += (_) => Recalculate();
        wire.OnClose += () =>
        {
            var pieces = _pieces;
            var peerPieces = wire.PeerPieces;
            if (peerPieces == null) return;
            int n = Math.Min(_numPieces, Math.Min(peerPieces.Length, pieces.Length));
            for (int i = 0; i < n; i++)
            {
                if (peerPieces[i])
                    pieces[i] = Math.Max(0, pieces[i] - 1);
            }
        };
    }

    private void CleanupWireEvents(Wire wire)
    {
        // In C# we can't easily remove specific lambda handlers,
        // but wire destruction will clean up naturally.
        // For long-lived RarityMaps, track handlers explicitly if needed.
    }
}
