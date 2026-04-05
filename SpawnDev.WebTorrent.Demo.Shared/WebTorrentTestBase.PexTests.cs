using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Pex_EncodeAdded_CompactFormat()
    {
        var buf = new byte[6];
        var ok = UtPexExtension.EncodeCompactIPv4("192.168.1.100:6881", buf, 0);
        if (!ok) throw new Exception("Encode failed");
        if (buf[0] != 192 || buf[1] != 168 || buf[2] != 1 || buf[3] != 100)
            throw new Exception($"Wrong IP bytes: {buf[0]}.{buf[1]}.{buf[2]}.{buf[3]}");
        int port = (buf[4] << 8) | buf[5];
        if (port != 6881) throw new Exception($"Wrong port: {port}");
    }

    [TestMethod]
    public async Task Pex_DecodeAdded_ParsesPeers()
    {
        var buf = new byte[12]; // 2 peers
        UtPexExtension.EncodeCompactIPv4("10.0.0.1:51413", buf, 0);
        UtPexExtension.EncodeCompactIPv4("172.16.0.5:6881", buf, 6);

        var p1 = UtPexExtension.DecodeCompactIPv4(buf, 0);
        var p2 = UtPexExtension.DecodeCompactIPv4(buf, 6);
        if (p1 != "10.0.0.1:51413") throw new Exception($"Peer 1 wrong: {p1}");
        if (p2 != "172.16.0.5:6881") throw new Exception($"Peer 2 wrong: {p2}");
    }

    [TestMethod]
    public async Task Pex_Flags_RoundTrip()
    {
        byte flags = UtPexExtension.FlagPrefersEncryption | UtPexExtension.FlagSupportsUtp;
        if ((flags & UtPexExtension.FlagPrefersEncryption) == 0)
            throw new Exception("Encryption flag not set");
        if ((flags & UtPexExtension.FlagSupportsUtp) == 0)
            throw new Exception("UTP flag not set");
        if ((flags & UtPexExtension.FlagIsSender) != 0)
            throw new Exception("Sender flag should not be set");
    }

    [TestMethod]
    public async Task Pex_CompactEncodeDecode_AllOctets()
    {
        // Test with addresses that exercise all byte values
        var buf = new byte[6];
        UtPexExtension.EncodeCompactIPv4("255.0.128.1:65535", buf, 0);
        var decoded = UtPexExtension.DecodeCompactIPv4(buf, 0);
        if (decoded != "255.0.128.1:65535") throw new Exception($"Mismatch: {decoded}");
    }

    [TestMethod]
    public async Task Pex_ZeroPort_Handled()
    {
        var buf = new byte[6];
        UtPexExtension.EncodeCompactIPv4("1.2.3.4:0", buf, 0);
        var decoded = UtPexExtension.DecodeCompactIPv4(buf, 0);
        if (decoded != "1.2.3.4:0") throw new Exception($"Zero port mismatch: {decoded}");
    }
}
