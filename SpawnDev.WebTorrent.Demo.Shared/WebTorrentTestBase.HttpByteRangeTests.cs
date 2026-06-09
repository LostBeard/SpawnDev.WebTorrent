using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Unit coverage for <see cref="HttpByteRange.ParseSingle"/> — the RFC 7233 §4.1 byte-range parser the
/// web-seed servers (<c>WebSeedServer</c> + the HuggingFace proxy) use to size 206 responses.
/// <para>
/// Regression headline: an explicit last-byte-pos PAST the end of the file used to be honored verbatim, so
/// the server promised a <c>Content-Length</c> larger than the bytes it could stream. The body closed short
/// and a browser <c>fetch()</c> rejected the entire response with <c>net::ERR_CONTENT_LENGTH_MISMATCH</c> —
/// which made web-seed piece downloads (e.g. an SD-Turbo model load from the hub) fail. The fix clamps the
/// end to <c>length-1</c>. These are pure (no I/O / no network) so they run identically on every backend.
/// </para>
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private static void ExpectSatisfiable(string header, long len, long expStart, long expEnd)
    {
        var r = HttpByteRange.ParseSingle(header, len, out long start, out long end);
        if (r != HttpByteRange.Result.Satisfiable)
            throw new Exception($"'{header}' (len={len}): expected Satisfiable, got {r}");
        if (start != expStart || end != expEnd)
            throw new Exception($"'{header}' (len={len}): got {start}-{end}, expected {expStart}-{expEnd}");
    }

    private static void ExpectResult(string? header, long len, HttpByteRange.Result exp)
    {
        var r = HttpByteRange.ParseSingle(header, len, out _, out _);
        if (r != exp)
            throw new Exception($"'{header ?? "<null>"}' (len={len}): expected {exp}, got {r}");
    }

    /// <summary>THE regression: an over-EOF last-byte-pos must clamp to the last real byte, NOT promise more.
    /// Mirrors the exact live-hub repro on tokenizer/vocab.json (1,059,962 bytes asked for bytes=1059952-1060961,
    /// 1000 past EOF) — which promised Content-Length 1010 but could stream only 10.</summary>
    [TestMethod]
    public Task HttpByteRange_OverEofEnd_ClampsToLastByte_NoOverPromise()
    {
        const long len = 1059962;
        var r = HttpByteRange.ParseSingle("bytes=1059952-1060961", len, out long start, out long end);
        if (r != HttpByteRange.Result.Satisfiable)
            throw new Exception($"expected Satisfiable, got {r}");
        if (start != 1059952) throw new Exception($"start={start}, expected 1059952");
        if (end != len - 1) throw new Exception($"end={end}, expected {len - 1} (clamped to EOF)");
        long contentLength = end - start + 1;
        if (contentLength != 10)
            throw new Exception($"Content-Length={contentLength}, expected 10 (the actual streamable bytes, not 1010)");

        // end exactly one past EOF is the boundary case.
        ExpectSatisfiable("bytes=0-100", 100, 0, 99);
        // a huge end (e.g. a full nominal piece beyond a partial tail) clamps too.
        ExpectSatisfiable("bytes=1700000000-1733430198", 1733430199, 1700000000, 1733430198);
        return Task.CompletedTask;
    }

    /// <summary>Ordinary in-bounds ranges (explicit, open-ended, whole-file, exact-tail-byte) pass through
    /// with the correct inclusive bounds.</summary>
    [TestMethod]
    public Task HttpByteRange_InBounds_PreservesBounds()
    {
        ExpectSatisfiable("bytes=10-19", 100, 10, 19);   // explicit interior
        ExpectSatisfiable("bytes=0-99", 100, 0, 99);     // exact whole file
        ExpectSatisfiable("bytes=50-", 100, 50, 99);     // open-ended to EOF
        ExpectSatisfiable("bytes=0-", 100, 0, 99);       // open-ended whole file
        ExpectSatisfiable("bytes=99-99", 100, 99, 99);   // single tail byte (the 0-0 size-probe pattern)
        ExpectSatisfiable("bytes=0-0", 100, 0, 0);       // single head byte
        return Task.CompletedTask;
    }

    /// <summary>Suffix ranges ("last N bytes") resolve correctly, including a suffix larger than the file.</summary>
    [TestMethod]
    public Task HttpByteRange_SuffixRanges_Resolve()
    {
        ExpectSatisfiable("bytes=-10", 100, 90, 99);   // last 10 bytes
        ExpectSatisfiable("bytes=-100", 100, 0, 99);   // suffix == length
        ExpectSatisfiable("bytes=-500", 100, 0, 99);   // suffix > length -> whole file
        return Task.CompletedTask;
    }

    /// <summary>A first-byte-pos at or past EOF is unsatisfiable (caller returns 416), distinct from a
    /// merely over-long END (which clamps).</summary>
    [TestMethod]
    public Task HttpByteRange_StartAtOrPastEof_IsUnsatisfiable()
    {
        ExpectResult("bytes=100-200", 100, HttpByteRange.Result.Unsatisfiable); // start == length
        ExpectResult("bytes=500-600", 100, HttpByteRange.Result.Unsatisfiable); // start past length
        ExpectResult("bytes=100-", 100, HttpByteRange.Result.Unsatisfiable);    // open-ended start at EOF
        ExpectResult("bytes=0-0", 0, HttpByteRange.Result.Unsatisfiable);       // empty representation
        return Task.CompletedTask;
    }

    /// <summary>Absent / non-bytes / malformed / multi-range / inverted headers return None so the caller
    /// serves the full representation (HTTP 200) rather than a garbage 206.</summary>
    [TestMethod]
    public Task HttpByteRange_UnusableHeaders_ReturnNone()
    {
        ExpectResult(null, 100, HttpByteRange.Result.None);
        ExpectResult("", 100, HttpByteRange.Result.None);
        ExpectResult("items=0-9", 100, HttpByteRange.Result.None);       // wrong unit
        ExpectResult("bytes=abc-def", 100, HttpByteRange.Result.None);   // non-numeric
        ExpectResult("bytes=0-9,20-29", 100, HttpByteRange.Result.None); // multi-range (unsupported here)
        ExpectResult("bytes=50-20", 100, HttpByteRange.Result.None);     // end < start
        ExpectResult("bytes=-0", 100, HttpByteRange.Result.None);        // zero-length suffix
        return Task.CompletedTask;
    }
}
