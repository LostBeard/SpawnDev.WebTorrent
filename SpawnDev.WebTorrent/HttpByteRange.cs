namespace SpawnDev.WebTorrent;

/// <summary>
/// Parsing for a single HTTP <c>Range</c> request header value, per RFC 7233 §2.1 / §4.1.
/// <para>
/// The web-seed servers (<c>WebSeedServer</c> and the HuggingFace proxy) hand a 206 response a
/// <c>Content-Length</c> equal to the requested byte count. If that count is taken from an explicit
/// last-byte-pos that runs PAST the end of the file, the server promises more bytes than it can stream,
/// the body closes short, and a browser <c>fetch()</c> rejects the whole response with
/// <c>net::ERR_CONTENT_LENGTH_MISMATCH</c> — so the web-seed piece never verifies and a model download
/// stalls. RFC 7233 §4.1 requires the server to treat a last-byte-pos &gt;= content length as the
/// remainder of the representation (i.e. clamp to <c>length-1</c>). This helper does exactly that.
/// </para>
/// </summary>
public static class HttpByteRange
{
    /// <summary>Outcome of parsing a Range header value against a known content length.</summary>
    public enum Result
    {
        /// <summary>No usable single byte-range (header absent, not <c>bytes=</c>, malformed, or multi-range).
        /// The caller should serve the full representation (HTTP 200).</summary>
        None,
        /// <summary>A satisfiable range. <c>start</c>/<c>end</c> are inclusive and clamped to the content;
        /// the caller should serve HTTP 206 with <c>Content-Length = end - start + 1</c>.</summary>
        Satisfiable,
        /// <summary>The first-byte-pos is at or beyond the content length (after parsing). The caller should
        /// return HTTP 416 (Range Not Satisfiable) with <c>Content-Range: bytes */{length}</c>.</summary>
        Unsatisfiable,
    }

    /// <summary>
    /// Parse a single <c>bytes=</c> range (<c>"bytes=start-end"</c>, <c>"bytes=start-"</c>, or a suffix
    /// <c>"bytes=-suffixLength"</c>) against <paramref name="contentLength"/>. On <see cref="Result.Satisfiable"/>
    /// the returned <paramref name="end"/> is clamped to <c>contentLength-1</c> so the caller never promises
    /// more than it can stream. Anything it does not understand returns <see cref="Result.None"/> (serve full).
    /// </summary>
    public static Result ParseSingle(string? rangeHeaderValue, long contentLength, out long start, out long end)
    {
        start = 0;
        end = contentLength - 1;

        if (string.IsNullOrEmpty(rangeHeaderValue)) return Result.None;
        var value = rangeHeaderValue.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return Result.None;

        var spec = value.Substring(6).Trim();
        // Multiple ranges ("bytes=0-9,20-29") are not supported here — serve full content.
        if (spec.Contains(',')) return Result.None;

        int dash = spec.IndexOf('-');
        if (dash < 0) return Result.None;

        var startPart = spec.Substring(0, dash).Trim();
        var endPart = spec.Substring(dash + 1).Trim();

        if (startPart.Length == 0)
        {
            // Suffix range "bytes=-N" => the last N bytes.
            if (!long.TryParse(endPart, out var suffix) || suffix <= 0) return Result.None;
            if (contentLength <= 0) return Result.Unsatisfiable;
            start = suffix >= contentLength ? 0 : contentLength - suffix;
            end = contentLength - 1;
            return Result.Satisfiable;
        }

        if (!long.TryParse(startPart, out start) || start < 0) return Result.None;

        // first-byte-pos at or past the end of the representation is unsatisfiable (RFC 7233 §4.1 -> 416).
        if (start >= contentLength) return Result.Unsatisfiable;

        if (endPart.Length == 0)
        {
            end = contentLength - 1; // open-ended "bytes=start-"
        }
        else
        {
            if (!long.TryParse(endPart, out end) || end < start) return Result.None;
            // Clamp an over-long last-byte-pos to the last real byte (this is the truncation fix).
            if (end > contentLength - 1) end = contentLength - 1;
        }

        return Result.Satisfiable;
    }
}
