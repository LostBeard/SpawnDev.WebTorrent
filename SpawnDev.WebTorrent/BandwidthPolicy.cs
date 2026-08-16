namespace SpawnDev.WebTorrent;

/// <summary>
/// High-level upload bandwidth policy. Translates to a concrete bytes/sec rate
/// for <see cref="WebTorrentClient.UploadRateLimiter"/>, while letting callers
/// express intent rather than pick a number.
///
/// Why this exists: BitTorrent seeders default to "as fast as possible," which
/// is the right call on a wired desktop with unlimited bandwidth and the wrong
/// call on a metered mobile hotspot, a battery-constrained laptop on cellular,
/// or a residential connection where saturating upload kills latency for
/// everyone else in the house. <see cref="BandwidthPolicy"/> captures the four
/// common deployment shapes and a per-call escape hatch.
/// </summary>
public enum BandwidthPolicy
{
    /// <summary>Default. No upload throttle - mirror legacy behavior. Use on
    /// wired desktops, dedicated servers, or anywhere the operator has already
    /// chosen the upload ceiling at the OS / network level.</summary>
    Unlimited = 0,

    /// <summary>~256 KiB/s upload ceiling. Won't saturate residential cable
    /// (typical 5-25 Mbps upstream), keeps room for video calls / gaming.
    /// Reasonable default for a torrent client running in the background on a
    /// shared home connection.</summary>
    Conservative = 1,

    /// <summary>~64 KiB/s upload ceiling. Designed for cellular / metered
    /// connections where every byte costs money or counts against a cap.
    /// High enough to seed slowly (still earn ratio), low enough to not
    /// notice on a phone bill.</summary>
    Metered = 2,

    /// <summary>Stop seeding entirely (rate = 0). The client will accept
    /// connections and download but won't serve pieces. Useful when on a tight
    /// data plan or when the operator is mid-upload of something else and
    /// wants to free the entire upstream.</summary>
    SeedingDisabled = 3,

    /// <summary>Sentinel - paired with <see cref="WebTorrentClientOptions.UploadLimit"/>
    /// and <see cref="WebTorrentClient.ThrottleUpload"/> when the caller
    /// supplies an exact bytes/sec value. Choosing <see cref="Custom"/>
    /// without setting an explicit limit falls back to <see cref="Unlimited"/>.</summary>
    Custom = 4,
}

/// <summary>Conversions + auto-detection helpers for <see cref="BandwidthPolicy"/>.</summary>
public static class BandwidthPolicyExtensions
{
    /// <summary>Conservative ceiling: 256 KiB/sec.</summary>
    public const long ConservativeBytesPerSec = 256L * 1024L;

    /// <summary>Metered ceiling: 64 KiB/sec.</summary>
    public const long MeteredBytesPerSec = 64L * 1024L;

    /// <summary>
    /// Translate a policy to a concrete bytes-per-second value for the upload
    /// rate limiter. Returns -1 for unlimited, 0 for paused, &gt;0 for a
    /// throttle. <see cref="BandwidthPolicy.Custom"/> without an explicit
    /// override falls back to <c>-1</c> (unlimited) - the caller is expected
    /// to set <see cref="WebTorrentClientOptions.UploadLimit"/> alongside.
    /// </summary>
    public static long ToUploadBytesPerSec(this BandwidthPolicy policy) => policy switch
    {
        BandwidthPolicy.Unlimited => -1,
        BandwidthPolicy.Conservative => ConservativeBytesPerSec,
        BandwidthPolicy.Metered => MeteredBytesPerSec,
        BandwidthPolicy.SeedingDisabled => 0,
        BandwidthPolicy.Custom => -1,  // caller wires UploadLimit separately
        _ => -1,
    };

    /// <summary>
    /// Best-effort "what should we default to on this machine?" classification.
    /// Conservative for unknown / desktop, Unlimited for explicitly-wired
    /// connections in the future. Today returns
    /// <see cref="BandwidthPolicy.Unlimited"/> on every platform - this is the
    /// hook callers should opt INTO via <c>BandwidthPolicy.AutoDetect()</c>
    /// rather than the silent default, so 3.1.x consumers see no behavior
    /// change. The detection logic will fill in over time as platform-specific
    /// signals (WinRT IsConnectionCostMetered, browser
    /// <c>navigator.connection.saveData</c>, NetworkInterface speed) get
    /// wired up.
    /// </summary>
    public static BandwidthPolicy AutoDetect()
    {
        // Today: no signal source wired; return Unlimited so behavior matches
        // 3.1.x. Future enhancement points (left as comments to make the
        // extension surface visible to readers):
        //   - Windows desktop: NetworkInformation.GetInternetConnectionProfile().GetConnectionCost().NetworkCostType
        //     (UWP API, not in BCL net10.0; would need a Windows-specific helper assembly)
        //   - Linux/Mac: NetworkInterface.GetAllNetworkInterfaces() + name heuristics
        //     (`wwan*` / `ppp*` for cellular) - imprecise but better than nothing
        //   - Browser: navigator.connection.saveData (NetworkInformation API,
        //     gated on Chromium today; SpawnDev.SpawnJS would surface it)
        return BandwidthPolicy.Unlimited;
    }
}
