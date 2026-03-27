using System.Text;

namespace SpawnDev.WebTorrent.Bencode;

/// <summary>
/// Bencode encoder. Bencode is the encoding used by .torrent files.
/// Supports: strings, integers, lists, and dictionaries.
/// See BEP 3: https://www.bittorrent.org/beps/bep_0003.html
/// </summary>
public static class BencodeEncoder
{
    /// <summary>Encode a string: {length}:{value}</summary>
    public static string EncodeString(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";

    /// <summary>Encode a byte string: {length}:{bytes}</summary>
    public static byte[] EncodeBytes(byte[] value)
    {
        var prefix = Encoding.ASCII.GetBytes($"{value.Length}:");
        var result = new byte[prefix.Length + value.Length];
        Array.Copy(prefix, result, prefix.Length);
        Array.Copy(value, 0, result, prefix.Length, value.Length);
        return result;
    }

    /// <summary>Encode an integer: i{value}e</summary>
    public static string EncodeInt(long value) => $"i{value}e";

    /// <summary>Encode a list: l{items}e</summary>
    public static string EncodeList(IEnumerable<string> items) => $"l{string.Concat(items)}e";

    /// <summary>Encode a dictionary: d{key}{value}...e (keys must be sorted)</summary>
    public static string EncodeDictionary(SortedDictionary<string, string> dict)
    {
        var sb = new StringBuilder("d");
        foreach (var (key, value) in dict)
        {
            sb.Append(EncodeString(key));
            sb.Append(value); // value is already bencoded
        }
        sb.Append('e');
        return sb.ToString();
    }

    /// <summary>Encode a complete object to bytes.</summary>
    public static byte[] Encode(object value)
    {
        return Encoding.UTF8.GetBytes(EncodeObject(value));
    }

    private static string EncodeObject(object value)
    {
        return value switch
        {
            string s => EncodeString(s),
            long l => EncodeInt(l),
            int i => EncodeInt(i),
            byte[] b => EncodeString(Encoding.UTF8.GetString(b)), // simplified
            IList<object> list => EncodeList(list.Select(EncodeObject)),
            SortedDictionary<string, object> dict =>
                EncodeDictionary(new SortedDictionary<string, string>(
                    dict.ToDictionary(kv => kv.Key, kv => EncodeObject(kv.Value)))),
            _ => throw new ArgumentException($"Cannot bencode type: {value.GetType()}")
        };
    }
}
