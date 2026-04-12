using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Typed tracker protocol messages matching the JS bittorrent-tracker wire format.
/// Uses CharStringConverter for info_hash/peer_id/offer_id binary string encoding.
/// Properties use [JsonIgnore(WhenWritingNull)] to omit absent fields (required by some trackers).
/// Based on SpawnDev.BlazorJS.Rally's proven message types.
/// </summary>

/// <summary>Base announce message - sent to tracker with offers.</summary>
public class TrackerAnnounceMessage
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "announce";

    [JsonPropertyName("info_hash")]
    public string InfoHash { get; set; } = "";

    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = "";

    [JsonPropertyName("uploaded")]
    public long Uploaded { get; set; }

    [JsonPropertyName("downloaded")]
    public long Downloaded { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("left")]
    public long? Left { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("numwant")]
    public int NumWant { get; set; } = 10;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("offers")]
    public object[]? Offers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("trackerid")]
    public string? TrackerId { get; set; }
}

/// <summary>Answer message - sent back to tracker to relay to offering peer.</summary>
public class TrackerAnswerMessage
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "announce";

    [JsonPropertyName("info_hash")]
    public string InfoHash { get; set; } = "";

    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = "";

    [JsonPropertyName("to_peer_id")]
    public string ToPeerId { get; set; } = "";

    [JsonPropertyName("answer")]
    public object? Answer { get; set; }

    [JsonPropertyName("offer_id")]
    public string OfferId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("trackerid")]
    public string? TrackerId { get; set; }
}

/// <summary>Server announce response.</summary>
public class TrackerAnnounceResponse
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "announce";

    [JsonPropertyName("info_hash")]
    public string InfoHash { get; set; } = "";

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 120;

    [JsonPropertyName("complete")]
    public int Complete { get; set; }

    [JsonPropertyName("incomplete")]
    public int Incomplete { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("peers")]
    public object[]? Peers { get; set; }
}

/// <summary>Server offer/answer relay message.</summary>
public class TrackerRelayMessage
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "announce";

    [JsonPropertyName("info_hash")]
    public string InfoHash { get; set; } = "";

    [JsonPropertyName("peer_id")]
    public string PeerId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("offer")]
    public object? Offer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("answer")]
    public object? Answer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("offer_id")]
    public object? OfferId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("to_peer_id")]
    public string? ToPeerId { get; set; }
}

/// <summary>
/// Extension methods for binary string (latin1 char-per-byte) encoding.
/// Ported from SpawnDev.BlazorJS.Rally's CharStringExtensions.
/// </summary>
public static class CharStringExtensions
{
    /// <summary>Convert byte array to latin1 char string (each byte becomes a char).</summary>
    public static string ToCharString(this byte[] data) => new string(data.Select(b => (char)b).ToArray());

    /// <summary>Convert latin1 char string back to byte array.</summary>
    public static byte[] ToCharBytes(this string charString) => charString.Select(c => (byte)c).ToArray();

    /// <summary>Convert hex string to latin1 char string.</summary>
    public static string HexToCharString(this string hexString) => Convert.FromHexString(hexString).ToCharString();

    /// <summary>Convert latin1 char string to hex string.</summary>
    public static string CharToHexString(this string charString) => Convert.ToHexString(charString.ToCharBytes()).ToLowerInvariant();
}
