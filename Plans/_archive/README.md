# Plans archive

Plans whose deliverables have shipped to the live tree. Retained for historical reference — they record the engineering plan, the bugs that surfaced along the way, and the decisions that shipped. **Not current** — read the top-level `Plans/` or `Docs/` for today's state.

- `bep52-sha256-support.md` — Phase 1 (SHA-256 piece hashes, default for new torrents). Shipped pre-3.0.
- `bep52-phase2-execution.md` — Phase 2 (BEP 52 v2 — Merkle-tree piece verification, hybrid v1+v2, leaf-level base_layer=0 serving, V2HashRequestCoordinator). Shipped 3.0.x → 3.1.x.
- `PLAN-BEP52-External-Interop.md` — Step 4 runbook for external-client interop (qBittorrent / libtorrent 2.0 static + live-swarm both directions, JS WebTorrent live-swarm). All steps SHIPPED 2026-04-23 / 2026-04-24. See `Docs/qbittorrent-interop.md` for the current matrix.

For the live state of BEP 52 in the codebase: `Docs/bep52.md`, `Docs/bep52-example.md`, and the test partials in `SpawnDev.WebTorrent.Demo.Shared`.
