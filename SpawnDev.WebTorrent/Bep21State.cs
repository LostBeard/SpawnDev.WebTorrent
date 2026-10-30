namespace SpawnDev.WebTorrent;

/// <summary>BEP 21 partial-seed state and torrent-wide verified byte accounting.</summary>
public static class Bep21State
{
    public static bool IsPartialSeed(bool hasMetadata, bool paused, bool destroyed,
        IReadOnlyList<bool> bitfield, IEnumerable<(int From, int To)> selections,
        IEnumerable<int> critical)
    {
        if (!hasMetadata || paused || destroyed || bitfield.Count == 0) return false;
        bool hasPiece = false, missing = false;
        foreach (bool have in bitfield) { hasPiece |= have; missing |= !have; }
        if (!hasPiece || !missing) return false;
        // No selection means the standard picker still wants the entire torrent.
        var selected = selections.ToArray();
        if (selected.Length == 0) return false;
        foreach (var (from, to) in selected)
            for (int i = Math.Max(0, from); i <= to && i < bitfield.Count; i++)
                if (!bitfield[i]) return false;
        foreach (int i in critical)
            if (i >= 0 && i < bitfield.Count && !bitfield[i]) return false;
        return true;
    }

    public static long VerifiedBytes(IReadOnlyList<bool> bitfield, long length, int pieceLength)
    {
        if (length <= 0 || pieceLength <= 0) return 0;
        long total = 0;
        for (int i = 0; i < bitfield.Count; i++)
        {
            long offset = (long)i * pieceLength;
            if (offset >= length) break;
            if (bitfield[i]) total += Math.Min(pieceLength, length - offset);
        }
        return total;
    }
}
