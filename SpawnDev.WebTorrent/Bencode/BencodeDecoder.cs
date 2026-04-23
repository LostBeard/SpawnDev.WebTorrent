using System.Text;

namespace SpawnDev.WebTorrent.Bencode;

/// <summary>
/// Bencode decoder. Parses .torrent file format.
/// Returns (value, bytesConsumed) tuples for composable parsing.
/// </summary>
public static class BencodeDecoder
{
    /// <summary>
    /// Decode a bencode dictionary preserving raw binary keys. Required for
    /// BEP 52 <c>piece layers</c> where keys are 32-byte SHA-256 roots that
    /// are not valid UTF-8. Returns entries in source order; callers that need
    /// map-style lookup can wrap the list in a byte-array-keyed dictionary.
    /// </summary>
    public static (List<KeyValuePair<byte[], object>> value, int consumed) DecodeDictionaryRawKeys(byte[] data, int offset)
    {
        if (data[offset] != 'd') throw new InvalidOperationException($"Expected 'd' at {offset}");
        var entries = new List<KeyValuePair<byte[], object>>();
        int pos = offset + 1;

        while (pos < data.Length && data[pos] != 'e')
        {
            var (keyBytes, keyConsumed) = DecodeRawString(data, pos);
            pos += keyConsumed;
            var (value, valConsumed) = Decode(data, pos);
            pos += valConsumed;
            entries.Add(new KeyValuePair<byte[], object>(keyBytes, value));
        }

        if (pos >= data.Length) throw new InvalidOperationException("Unterminated dictionary");
        return (entries, pos + 1 - offset);
    }

    /// <summary>Decode a bencoded value from raw bytes.</summary>
    public static (object value, int consumed) Decode(byte[] data, int offset = 0)
    {
        if (offset >= data.Length) throw new InvalidOperationException("Unexpected end of data");

        return (char)data[offset] switch
        {
            'i' => DecodeIntAsObject(data, offset),
            'l' => DecodeList(data, offset),
            'd' => DecodeDictionary(data, offset),
            >= '0' and <= '9' => DecodeStringAsObject(data, offset),
            _ => throw new InvalidOperationException($"Invalid bencode byte at {offset}: 0x{data[offset]:X2}")
        };
    }

    /// <summary>Decode a bencode string: {length}:{bytes}</summary>
    public static (string value, int consumed) DecodeString(byte[] data, int offset)
    {
        var (bytes, consumed) = DecodeRawString(data, offset);
        return (Encoding.UTF8.GetString(bytes), consumed);
    }

    /// <summary>Decode a bencode string as raw bytes (for binary data like info hash).</summary>
    public static (byte[] value, int consumed) DecodeRawString(byte[] data, int offset)
    {
        int colonIdx = Array.IndexOf(data, (byte)':', offset);
        if (colonIdx < 0) throw new InvalidOperationException($"Missing ':' in string at offset {offset}");

        int length = int.Parse(Encoding.ASCII.GetString(data, offset, colonIdx - offset));
        if (length < 0) throw new InvalidOperationException($"Negative string length at offset {offset}");
        int start = colonIdx + 1;
        var value = new byte[length];
        Array.Copy(data, start, value, 0, length);
        return (value, start + length - offset);
    }

    /// <summary>Decode a bencode integer: i{value}e</summary>
    public static (long value, int consumed) DecodeInt(byte[] data, int offset)
    {
        if (data[offset] != 'i') throw new InvalidOperationException($"Expected 'i' at {offset}");
        int endIdx = Array.IndexOf(data, (byte)'e', offset + 1);
        if (endIdx < 0) throw new InvalidOperationException($"Missing 'e' for integer at {offset}");

        var intStr = Encoding.ASCII.GetString(data, offset + 1, endIdx - offset - 1);
        if (intStr == "-0") throw new InvalidOperationException("Negative zero is not allowed in bencode");
        if (intStr.Length > 1 && intStr[0] == '0') throw new InvalidOperationException("Leading zeros not allowed in bencode integers");
        if (intStr.Length > 2 && intStr[0] == '-' && intStr[1] == '0') throw new InvalidOperationException("Leading zeros not allowed in bencode integers");

        long value = long.Parse(intStr);
        return (value, endIdx + 1 - offset);
    }

    /// <summary>Decode a bencode list: l{items}e</summary>
    public static (List<object> value, int consumed) DecodeList(byte[] data, int offset)
    {
        if (data[offset] != 'l') throw new InvalidOperationException($"Expected 'l' at {offset}");
        var list = new List<object>();
        int pos = offset + 1;

        while (pos < data.Length && data[pos] != 'e')
        {
            var (item, consumed) = Decode(data, pos);
            list.Add(item);
            pos += consumed;
        }

        if (pos >= data.Length) throw new InvalidOperationException("Unterminated list");
        return (list, pos + 1 - offset); // +1 for 'e'
    }

    /// <summary>Decode a bencode dictionary: d{key}{value}...e</summary>
    public static (Dictionary<string, object> value, int consumed) DecodeDictionary(byte[] data, int offset)
    {
        if (data[offset] != 'd') throw new InvalidOperationException($"Expected 'd' at {offset}");
        var dict = new Dictionary<string, object>();
        int pos = offset + 1;

        while (pos < data.Length && data[pos] != 'e')
        {
            var (key, keyConsumed) = DecodeString(data, pos);
            pos += keyConsumed;
            var (value, valConsumed) = Decode(data, pos);
            pos += valConsumed;
            dict[key] = value;
        }

        if (pos >= data.Length) throw new InvalidOperationException("Unterminated dictionary");
        return (dict, pos + 1 - offset);
    }

    /// <summary>Decode a bencode dictionary, also tracking the raw bytes of specified keys.</summary>
    public static (Dictionary<string, object> value, Dictionary<string, (int offset, int length)> rawKeys, int consumed)
        DecodeDictionaryWithRawKeys(byte[] data, int offset, params string[] trackKeys)
    {
        if (data[offset] != 'd') throw new InvalidOperationException($"Expected 'd' at {offset}");
        var dict = new Dictionary<string, object>();
        var rawKeys = new Dictionary<string, (int offset, int length)>();
        var trackSet = new HashSet<string>(trackKeys);
        int pos = offset + 1;

        while (pos < data.Length && data[pos] != 'e')
        {
            var (key, keyConsumed) = DecodeString(data, pos);
            pos += keyConsumed;

            int valueStart = pos;
            var (value, valConsumed) = Decode(data, pos);
            pos += valConsumed;

            dict[key] = value;
            if (trackSet.Contains(key))
                rawKeys[key] = (valueStart, valConsumed);
        }

        if (pos >= data.Length) throw new InvalidOperationException("Unterminated dictionary");
        return (dict, rawKeys, pos + 1 - offset);
    }

    private static (object value, int consumed) DecodeIntAsObject(byte[] data, int offset)
    {
        var (val, consumed) = DecodeInt(data, offset);
        return (val, consumed);
    }

    private static (object value, int consumed) DecodeStringAsObject(byte[] data, int offset)
    {
        var (val, consumed) = DecodeRawString(data, offset);
        return (val, consumed);
    }
}
