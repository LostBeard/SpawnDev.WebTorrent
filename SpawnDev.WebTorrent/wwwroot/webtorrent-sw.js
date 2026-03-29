/*! SpawnDev.WebTorrent — Service Worker + Blazor Loader */
/*
 * Combined service worker and Blazor loader for SpawnDev.WebTorrent apps.
 *
 * In the page context (window):
 *   - Registers this file as a service worker
 *   - Waits for SW to be ready (for COI headers + streaming)
 *   - Reloads once to apply Cross-Origin-Isolation headers
 *   - Then loads Blazor WebAssembly
 *
 * As a service worker (self):
 *   - Adds COOP/COEP headers for SharedArrayBuffer support
 *   - Intercepts /webtorrent/ requests for torrent streaming
 *   - Forwards streaming requests to the main window via MessageChannel
 *   - Supports HTTP range requests for video/audio seeking
 *
 * Usage: Replace <script src="_framework/blazor.webassembly.js"> with:
 *   <script src="webtorrent-sw.js"></script>
 *
 * Deploys to app root via StaticWebAssetBasePath="/".
 */

if (typeof window !== 'undefined') {
    // --- Running as a regular script in the page context ---

    var verbose = false;
    function consoleLog(...args) {
        if (!verbose) return;
        console.log("[COI]", ...args);
    }

    // Helper to check if Blazor script tag exists in the HTML
    function hasBlazorScript() {
        return !!document.querySelector('script[src*="blazor.webassembly"]');
    }

    // Load Blazor dynamically (only needed when no static script tag in HTML)
    function loadBlazor() {
        if (hasBlazorScript()) return;
        var s = document.createElement("script");
        s.src = "_framework/blazor.webassembly.js";
        document.body.appendChild(s);
    }

    if (window.crossOriginIsolated) {
        // Already cross-origin isolated — SharedArrayBuffer available
        consoleLog("[COI] Cross-origin isolated ✓");
        sessionStorage.removeItem("coi-reload-count");
        loadBlazor();
    } else if ("serviceWorker" in navigator) {
        // Not yet isolated — register/activate the SW, then reload ONCE to apply headers.
        // Use sessionStorage to prevent infinite reload loops: if COI still fails after
        // reloading, stop retrying and load Blazor without SharedArrayBuffer.
        var reloadKey = "coi-reload-count";
        var reloadCount = parseInt(sessionStorage.getItem(reloadKey) || "0", 10);

        if (reloadCount < 2) {
            // Register the SW (idempotent if already registered)
            navigator.serviceWorker
                .register(window.document.currentScript.src)
                .then(function (reg) {
                    consoleLog("[COI] Service worker registered:", reg.scope);
                })
                .catch(function (err) {
                    console.error("[COI] Service worker registration failed:", err);
                    // Registration failed — load Blazor without COI
                    loadBlazor();
                });

            // Wait for SW to be ready, then reload to pick up COI headers.
            // Timeout after 5s — if the SW doesn't activate in time, load Blazor anyway.
            var reloaded = false;
            var doReload = function () {
                if (reloaded) return;
                reloaded = true;
                sessionStorage.setItem(reloadKey, String(reloadCount + 1));
                consoleLog("[COI] Reloading to apply COI headers (attempt " + (reloadCount + 1) + ")");
                window.location.reload();
            };

            navigator.serviceWorker.ready.then(doReload);
            setTimeout(function () {
                if (!reloaded && navigator.serviceWorker.controller) {
                    // SW is controlling but ready didn't fire — force reload
                    doReload();
                } else if (!reloaded) {
                    consoleLog("[COI] Service worker not ready after 5s — loading without COI");
                    loadBlazor();
                }
            }, 5000);
        } else {
            // Already tried reloading — COI isn't working, proceed without it.
            // Clear the counter so next fresh navigation can try again.
            console.warn("[COI] Cross-origin isolation failed after " + reloadCount +
                " reload(s) — SharedArrayBuffer unavailable. Wasm limited to 1 worker.");
            sessionStorage.removeItem(reloadKey);
            loadBlazor();
        }
    } else {
        consoleLog("[COI] Service workers not supported — SharedArrayBuffer unavailable");
        loadBlazor();
    }

} else {
    // ═══════════════════════════════════════════════════════════
    //  SERVICE WORKER CONTEXT — COI Headers + Torrent Streaming
    // ═══════════════════════════════════════════════════════════

    self.addEventListener('install', () => self.skipWaiting());
    self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

    self.addEventListener('fetch', (event) => {
        if (event.request.cache === 'only-if-cached' && event.request.mode !== 'same-origin') {
            return;
        }

        const url = new URL(event.request.url);
        if (url.origin !== self.location.origin) {
            return;
        }

        // Health check — lets clients verify the SW is active and intercepting
        if (url.pathname.endsWith('/webtorrent-sw-check')) {
            event.respondWith(new Response(JSON.stringify({
                name: 'SpawnDev.WebTorrent',
                active: true,
                scope: self.registration.scope,
            }), {
                status: 200,
                headers: { 'Content-Type': 'application/json' },
            }));
            return;
        }

        // WebTorrent streaming — intercept /webtorrent/ paths
        if (url.pathname.includes('/webtorrent/')) {
            event.respondWith(handleWebtorrentStream(event));
            return;
        }

        // COI headers for all other same-origin requests
        event.respondWith(addCoiHeaders(event.request));
    });

    async function addCoiHeaders(request) {
        try {
            const response = await fetch(request);
            const headers = new Headers(response.headers);
            headers.set('Cross-Origin-Embedder-Policy', 'credentialless');
            headers.set('Cross-Origin-Opener-Policy', 'same-origin');
            return new Response(response.body, {
                status: response.status,
                statusText: response.statusText,
                headers,
            });
        } catch (e) {
            return new Response('Service Worker fetch failed', { status: 502 });
        }
    }

    // ── WebTorrent streaming ──
    // Protocol (matches webtorrent/webtorrent worker-server.js):
    // 1. SW posts request details to client window via MessageChannel
    // 2. Client responds with { status, headers, body } where body is 'STREAM' or data
    // 3. If 'STREAM': SW creates ReadableStream, pulls chunks via port messages
    // 4. Client sends Uint8Array chunks on pull (true), null = end, false = cancel

    async function handleWebtorrentStream(event) {
        const allClients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        if (allClients.length === 0) {
            return new Response('No client available', { status: 503 });
        }

        const request = event.request;

        // Send to the client that made the request (not a random client)
        const clientId = event.clientId || event.resultingClientId;
        let client = clientId ? await self.clients.get(clientId) : null;
        if (!client) client = allClients[0]; // fallback

        const mc = new MessageChannel();
        const [data, port] = await new Promise((resolve) => {
            mc.port1.onmessage = (evt) => resolve([evt.data, mc.port1]);
            client.postMessage({
                type: 'webtorrent',
                url: request.url,
                method: request.method,
                headers: Object.fromEntries(request.headers.entries()),
                scope: self.registration.scope,
                destination: request.destination,
            }, [mc.port2]);
        });

        if (!data) {
            return new Response('No response from client', { status: 500 });
        }

        // Direct response (small files, errors, non-streamable)
        if (data.body !== 'STREAM') {
            port.onmessage = null;
            return new Response(data.body, {
                status: data.status || 200,
                headers: data.headers || {},
            });
        }

        // Streaming response — pull chunks from client on demand
        let timeOut = null;
        const portTimeoutDuration = 5000;

        const cleanup = () => {
            port.postMessage(false);
            clearTimeout(timeOut);
            port.onmessage = null;
        };

        const stream = new ReadableStream({
            async pull(controller) {
                return new Promise((resolve) => {
                    port.onmessage = (msg) => {
                        if (msg.data) {
                            controller.enqueue(msg.data);
                        } else {
                            cleanup();
                            controller.close();
                        }
                        resolve();
                    };
                    clearTimeout(timeOut);
                    if (data.destination !== 'document') {
                        timeOut = setTimeout(() => {
                            cleanup();
                            resolve();
                        }, portTimeoutDuration);
                    }
                    port.postMessage(true);
                });
            },
            cancel() {
                cleanup();
            }
        });

        return new Response(stream, {
            status: data.status || 200,
            headers: data.headers || {},
        });
    }
}
