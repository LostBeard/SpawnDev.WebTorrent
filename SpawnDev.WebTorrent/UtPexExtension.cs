using System.Text;

namespace SpawnDev.WebTorrent;

/// <summary>
/// ut_pex wire extension (BEP 11) — Peer Exchange.
/// Exchanges connected/disconnected peer lists between peers.
/// Improvements over original: 65s send interval, rate limiting,
/// dedup tracking, 50-peer-per-message cap (matching JS reference).
/// </summary>
public class UtPexExtension : IWireExtension
{
    public const int PexIntervalMs = 65_000;      // JS: 65 seconds between sends
    public const int PexMinIntervalMs = 60_000;    // disconnect if remote sends faster
    public const int PexMaxPeers = 50;             // max peers per message

    public string Name => "ut_pex";

    public record PexPeerInfo(string Address, byte Flags = 0);

    // Flags (BEP 11)
    public const byte FlagPrefersEncryption = 0x01;
    public const byte FlagIsSender = 0x02;
    public const byte FlagSupportsUtp = 0x04;
    public const byte FlagSupportsUtHolepunch = 0x08;
    public const byte FlagIsReachable = 0x10;

    /// <summary>Fired when new peers are received via PEX.</summary>
    public event Action<List<PexPeerInfo>>? OnPeersReceived;

    /// <summary>Fired when peers are reported as dropped via PEX.</summary>
    public event Action<List<string>>? OnPeersDropped;

    private Wire? _wire;
    private bool _isSupported;
    private DateTime _lastReceiveTime = DateTime.MinValue;

    // Dedup tracking — don't re-advertise what the remote already told us
    private readonly HashSet<string> _remoteAddedPeers = new();

    public bool IsSupported => _isSupported;

    /// <summary>Set the wire this extension operates on (called after construction).</summary>
    public void SetWire(Wire wire) { _wire = wire; }

    public void OnHandshake(string infoHash, string peerId, WireExtensions extensions) { }

    public void OnExtendedHandshake(Dictionary<string, object> handshake)
    {
        // Check if remote supports ut_pex
        if (handshake.TryGetValue("m", out var mObj) && mObj is Dictionary<string, object> m)
            _isSupported = m.ContainsKey("ut_pex");
    }

    public void OnMessage(byte[] buf)
    {
        try
        {
            // Rate limit: disconnect if messages arrive faster than 60s
            var now = DateTime.UtcNow;
            if (_lastReceiveTime != DateTime.MinValue &&
                (now - _lastReceiveTime).TotalMilliseconds < PexMinIntervalMs)
            {
                _wire?.Destroy();
                return;
            }
            _lastReceiveTime = now;

            var (decoded, _) = Bencode.BencodeDecoder.Decode(buf, 0);
            if (decoded is not Dictionary<string, object> msg) return;

            var peers = new List<PexPeerInfo>();

            // Parse added IPv4 peers (6 bytes each: 4 IP + 2 port)
            byte[] addedBytes = ResolveByteField(msg, "added");
            byte[] flagsBytes = ResolveByteField(msg, "added.f");

            if (addedBytes.Length > 0)
            {
                int peerCount = 0;
                for (int i = 0; i + 6 <= addedBytes.Length; i += 6)
                {
                    var ip = $"{addedBytes[i]}.{addedBytes[i + 1]}.{addedBytes[i + 2]}.{addedBytes[i + 3]}";
                    var port = (addedBytes[i + 4] << 8) | addedBytes[i + 5];
                    byte flags = peerCount < flagsBytes.Length ? flagsBytes[peerCount] : (byte)0;
                    var addr = $"{ip}:{port}";
                    peers.Add(new PexPeerInfo(addr, flags));
                    _remoteAddedPeers.Add(addr);
                    peerCount++;
                }
            }

            // Parse added IPv6 peers (18 bytes each: 16 IP + 2 port)
            byte[] added6Bytes = ResolveByteField(msg, "added6");
            if (added6Bytes.Length > 0)
            {
                for (int i = 0; i + 18 <= added6Bytes.Length; i += 18)
                {
                    var ipBytes = new byte[16];
                    Array.Copy(added6Bytes, i, ipBytes, 0, 16);
                    var ip = new System.Net.IPAddress(ipBytes).ToString();
                    var port = (added6Bytes[i + 16] << 8) | added6Bytes[i + 17];
                    var addr = $"[{ip}]:{port}";
                    peers.Add(new PexPeerInfo(addr));
                    _remoteAddedPeers.Add(addr);
                }
            }

            if (peers.Count > 0)
                OnPeersReceived?.Invoke(peers);

            // Parse dropped peers
            var droppedBytes = ResolveByteField(msg, "dropped");
            if (droppedBytes.Length > 0)
            {
                var dropped = new List<string>();
                for (int i = 0; i + 6 <= droppedBytes.Length; i += 6)
                {
                    var ip = $"{droppedBytes[i]}.{droppedBytes[i + 1]}.{droppedBytes[i + 2]}.{droppedBytes[i + 3]}";
                    var port = (droppedBytes[i + 4] << 8) | droppedBytes[i + 5];
                    var addr = $"{ip}:{port}";
                    dropped.Add(addr);
                    _remoteAddedPeers.Remove(addr);
                }
                if (dropped.Count > 0)
                    OnPeersDropped?.Invoke(dropped);
            }
        }
        catch { }
    }

    /// <summary>Send an outbound PEX message with added and dropped peers.</summary>
    public async Task SendPexAsync(List<PexPeerInfo> added, List<string> dropped)
    {
        if (_wire == null || !_isSupported) return;

        // Cap at PexMaxPeers
        if (added.Count > PexMaxPeers) added = added.Take(PexMaxPeers).ToList();
        if (dropped.Count > PexMaxPeers) dropped = dropped.Take(PexMaxPeers).ToList();

        // Filter out peers the remote already told us about (dedup)
        added = added.Where(p => !_remoteAddedPeers.Contains(p.Address)).ToList();

        var dict = new Dictionary<string, object>();

        if (added.Count > 0)
        {
            var addedCompact = new byte[added.Count * 6];
            var addedFlags = new byte[added.Count];
            for (int i = 0; i < added.Count; i++)
            {
                EncodeCompactIPv4(added[i].Address, addedCompact, i * 6);
                addedFlags[i] = added[i].Flags;
            }
            dict["added"] = addedCompact;
            dict["added.f"] = addedFlags;
        }
        else
        {
            dict["added"] = Array.Empty<byte>();
        }

        if (dropped.Count > 0)
        {
            var droppedCompact = new byte[dropped.Count * 6];
            for (int i = 0; i < dropped.Count; i++)
                EncodeCompactIPv4(dropped[i], droppedCompact, i * 6);
            dict["dropped"] = droppedCompact;
        }
        else
        {
            dict["dropped"] = Array.Empty<byte>();
        }

        await _wire.Extended("ut_pex", Bencode.BencodeEncoder.Encode(dict));
    }

    /// <summary>Encode an "ip:port" string into 6-byte compact IPv4 format.</summary>
    public static bool EncodeCompactIPv4(string address, byte[] buffer, int offset)
    {
        var parts = address.Split(':');
        if (parts.Length != 2) return false;
        var ipParts = parts[0].Split('.');
        if (ipParts.Length != 4) return false;
        buffer[offset] = byte.Parse(ipParts[0]);
        buffer[offset + 1] = byte.Parse(ipParts[1]);
        buffer[offset + 2] = byte.Parse(ipParts[2]);
        buffer[offset + 3] = byte.Parse(ipParts[3]);
        int port = int.Parse(parts[1]);
        buffer[offset + 4] = (byte)(port >> 8);
        buffer[offset + 5] = (byte)(port & 0xFF);
        return true;
    }

    /// <summary>Decode 6 bytes at offset into an "ip:port" string.</summary>
    public static string DecodeCompactIPv4(byte[] buffer, int offset)
    {
        var ip = $"{buffer[offset]}.{buffer[offset + 1]}.{buffer[offset + 2]}.{buffer[offset + 3]}";
        var port = (buffer[offset + 4] << 8) | buffer[offset + 5];
        return $"{ip}:{port}";
    }

    private static byte[] ResolveByteField(Dictionary<string, object> msg, string key)
    {
        if (msg.TryGetValue(key, out var obj))
        {
            if (obj is byte[] b) return b;
            if (obj is string s) return Encoding.Latin1.GetBytes(s);
        }
        return Array.Empty<byte>();
    }
}
