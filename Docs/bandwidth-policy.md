# Bandwidth Policy

`BandwidthPolicy` is the high-level upload throttle for `WebTorrentClient`. It lets callers express **intent** (be unlimited / be conservative on a residential connection / be metered on cellular / pause seeding entirely) without picking a bytes/sec number.

Shipped in **3.2.0** (2026-04-25). Default is `Unlimited` — existing 3.1.x code keeps running unchanged.

## Why this exists

BitTorrent seeders default to "as fast as possible", which is the right call on a wired desktop with unlimited bandwidth and the wrong call on:

- A metered mobile hotspot where every byte costs money or counts against a cap
- A battery-constrained laptop on cellular
- A residential connection where saturating upload kills latency for everyone else in the house

`UploadLimit` (`long`, bytes/sec) was already there for the precise case. `BandwidthPolicy` is the convenience layer for the four common deployment shapes plus an explicit-`Custom` escape hatch.

## The enum

```csharp
public enum BandwidthPolicy
{
    /// Default. No upload throttle - mirror legacy behavior.
    /// Use on wired desktops, dedicated servers, or anywhere the operator
    /// has already chosen the upload ceiling at the OS / network level.
    Unlimited = 0,

    /// ~256 KiB/s upload ceiling. Won't saturate residential cable
    /// (typical 5-25 Mbps upstream), keeps room for video calls / gaming.
    /// Reasonable default for a torrent client running in the background
    /// on a shared home connection.
    Conservative = 1,

    /// ~64 KiB/s upload ceiling. Designed for cellular / metered
    /// connections where every byte costs money or counts against a cap.
    /// High enough to seed slowly (still earn ratio), low enough to not
    /// notice on a phone bill.
    Metered = 2,

    /// Stop seeding entirely (rate = 0). The client will accept
    /// connections and download but won't serve pieces.
    SeedingDisabled = 3,

    /// Sentinel - paired with WebTorrentClientOptions.UploadLimit
    /// and WebTorrentClient.ThrottleUpload when the caller supplies an
    /// exact bytes/sec value.
    Custom = 4,
}
```

## Translation table

| Policy | `UploadRateLimiter.Rate` | Comment |
|--------|--------------------------|---------|
| `Unlimited` | `-1` | No throttle (default) |
| `Conservative` | `262_144` (256 KiB/s) | `BandwidthPolicyExtensions.ConservativeBytesPerSec` |
| `Metered` | `65_536` (64 KiB/s) | `BandwidthPolicyExtensions.MeteredBytesPerSec` |
| `SeedingDisabled` | `0` | Paused |
| `Custom` | unchanged | Caller pins the rate via `UploadLimit` / `ThrottleUpload` |

Conversion is exposed as `policy.ToUploadBytesPerSec()`.

## Wiring

### Construct-time

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    BandwidthPolicy = BandwidthPolicy.Metered,   // 64 KiB/s upload ceiling
});
```

When `WebTorrentClientOptions.UploadLimit` is set explicitly (`>= 0`), it **wins** over the policy — the policy is the convenience layer, the explicit value is the override:

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    BandwidthPolicy = BandwidthPolicy.Custom,
    UploadLimit = 12_345 * 1024,   // exactly 12,345 KiB/s
});
// client.UploadRateLimiter.Rate == 12_345 * 1024
// client.BandwidthPolicy == BandwidthPolicy.Custom
```

### Runtime knob

```csharp
client.ApplyBandwidthPolicy(BandwidthPolicy.Unlimited);  // back to full speed
client.ApplyBandwidthPolicy(BandwidthPolicy.Metered);    // back to 64 KiB/s
client.ApplyBandwidthPolicy(BandwidthPolicy.SeedingDisabled);  // pause seeding
```

`ApplyBandwidthPolicy` updates `BandwidthPolicy` AND `UploadRateLimiter.Rate` in lockstep so the two can't drift. Passing `BandwidthPolicy.Custom` is a no-op for the rate — caller pins it via `ThrottleUpload`:

```csharp
client.ApplyBandwidthPolicy(BandwidthPolicy.Custom);
client.ThrottleUpload(500 * 1024);   // 500 KiB/s
```

### Direct rate (legacy path)

```csharp
client.ThrottleUpload(256 * 1024);   // 256 KiB/s
// equivalent to: client.UploadRateLimiter.Rate = 256 * 1024
// BandwidthPolicy is NOT updated; flip it explicitly if you care.
```

## Auto-detection (placeholder today)

`BandwidthPolicy.AutoDetect()` returns a recommended policy based on platform-specific signals. **Today it returns `Unlimited` on every platform** — so 3.1.x consumers see no behavior change unless they call this explicitly. The hook exists so the detection logic can fill in over time:

| Platform | Signal source | Status |
|----------|---------------|--------|
| Windows desktop | WinRT `NetworkInformation.GetInternetConnectionProfile().GetConnectionCost().NetworkCostType` | Not wired today (UWP API, not in BCL net10.0) |
| Linux / macOS | `NetworkInterface.GetAllNetworkInterfaces()` + name heuristics (`wwan*` / `ppp*`) | Not wired today |
| Browser | `navigator.connection.saveData` (NetworkInformation API) via SpawnDev.BlazorJS | Not wired today |

Until detection lands, opt in explicitly:

```csharp
// "Be conservative unless I say otherwise." - works today.
new WebTorrentClient(new WebTorrentClientOptions
{
    BandwidthPolicy = BandwidthPolicy.Conservative,
});

// What AutoDetect WILL do once wired - but for now do it manually:
var policy = OperatingSystem.IsBrowser()
    ? BandwidthPolicy.Conservative   // or read navigator.connection yourself
    : BandwidthPolicy.Unlimited;
new WebTorrentClient(new WebTorrentClientOptions { BandwidthPolicy = policy });
```

## What's NOT in the policy

- **Download throttle.** Use `WebTorrentClientOptions.DownloadLimit` (or `DownloadRateLimiter.Rate`) directly. Download throttling is rarer and the policy enum stays focused on upload.
- **Per-torrent overrides.** The policy is client-wide. To throttle a single torrent differently, set `WebTorrentClient.UploadRateLimiter` for the global limit and rely on the choke algorithm's per-peer fairness, or maintain separate clients.
- **Smart-piece prioritization.** Bandwidth-aware seeding could prioritize rare pieces over abundant ones when bandwidth is constrained — that's a future change to the choke-rotation algorithm, not part of this policy.

## Reference

- Source: [`SpawnDev.WebTorrent/BandwidthPolicy.cs`](../SpawnDev.WebTorrent/BandwidthPolicy.cs)
- Tests: [`PlaywrightMultiTest/DesktopWebRtcTest.cs`](../PlaywrightMultiTest/DesktopWebRtcTest.cs) → `Desktop_BandwidthPolicy_AppliesToUploadRateLimiter`, `_ExplicitUploadLimitWins`, `_ApplyAtRuntimeSwitchesRate`
- Call site: `WebTorrentClient` constructor in [`SpawnDev.WebTorrent/WebTorrentClient.cs`](../SpawnDev.WebTorrent/WebTorrentClient.cs)
- Related: `WebTorrentClient.UploadRateLimiter`, `WebTorrentClient.ThrottleUpload(long)`, `WebTorrentClient.ApplyBandwidthPolicy(BandwidthPolicy)`
