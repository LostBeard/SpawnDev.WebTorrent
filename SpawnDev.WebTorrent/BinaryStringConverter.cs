using System.Text;
using System.Text.Json;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Serializes objects to JSON with binary strings written as raw UTF-8 bytes,
/// not escaped as \u00XX for C1 control characters (0x80-0x9F).
/// Matches JS JSON.stringify behavior for BitTorrent tracker protocol compatibility.
/// </summary>
public static class BinaryJsonSerializer
{
    private static readonly JsonSerializerOptions _baseOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serialize to JSON matching JS wire format.
    /// UnsafeRelaxedJsonEscaping handles most chars, then we fix C1 control chars (0x80-0x9F)
    /// which System.Text.Json still escapes but JS JSON.stringify does not.
    /// </summary>
    public static string Serialize(object value, JsonSerializerOptions? baseOptions = null)
    {
        var opts = baseOptions ?? _baseOpts;
        var json = JsonSerializer.Serialize(value, opts);

        // Fix C1 control chars (0x80-0x9F) that System.Text.Json escapes as \u00XX
        // but JS JSON.stringify writes as literal UTF-8 bytes.
        // Only target 0x80-0xFF range. C0 chars (0x00-0x1F) stay escaped per JSON spec.
        var sb = new StringBuilder(json.Length);
        for (int i = 0; i < json.Length; i++)
        {
            if (i + 5 < json.Length && json[i] == '\\' && json[i + 1] == 'u' && json[i + 2] == '0' && json[i + 3] == '0')
            {
                // Parse the hex value
                var hex = json.Substring(i + 4, 2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int val) && val >= 0x80)
                {
                    sb.Append((char)val);
                    i += 5; // skip the 6-char \u00XX sequence
                    continue;
                }
            }
            sb.Append(json[i]);
        }
        return sb.ToString();
    }
}
