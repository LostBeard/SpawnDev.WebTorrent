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

    /// <summary>Encode a dictionary: d{key}{value}...e (keys sorted by raw bytes)</summary>
    public static string EncodeDictionary(IDictionary<string, string> dict)
    {
        var sb = new StringBuilder("d");
        foreach (var (key, value) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(EncodeString(key));
            sb.Append(value);
        }
        sb.Append('e');
        return sb.ToString();
    }

    /// <summary>Encode a complete object to bytes.</summary>
    public static byte[] Encode(object value)
    {
        var parts = new List<byte>();
        EncodeObjectBytes(value, parts);
        return parts.ToArray();
    }

    private static void EncodeObjectBytes(object value, List<byte> output)
    {
        switch (value)
        {
            case byte[] b:
                output.AddRange(EncodeBytes(b));
                break;
            case string s:
                output.AddRange(Encoding.UTF8.GetBytes(EncodeString(s)));
                break;
            case long l:
                output.AddRange(Encoding.UTF8.GetBytes(EncodeInt(l)));
                break;
            case int i:
                output.AddRange(Encoding.UTF8.GetBytes(EncodeInt(i)));
                break;
            case IList<object> list:
                output.AddRange(Encoding.ASCII.GetBytes("l"));
                foreach (var item in list)
                    EncodeObjectBytes(item, output);
                output.AddRange(Encoding.ASCII.GetBytes("e"));
                break;
            case IDictionary<string, object> dict:
                output.AddRange(Encoding.ASCII.GetBytes("d"));
                foreach (var kv in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    output.AddRange(Encoding.UTF8.GetBytes(EncodeString(kv.Key)));
                    EncodeObjectBytes(kv.Value, output);
                }
                output.AddRange(Encoding.ASCII.GetBytes("e"));
                break;
            default:
                throw new ArgumentException($"Cannot bencode type: {value.GetType()}");
        }
    }
}
