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
    // ═══════════════════════════════════════════════════════════
    //  PAGE CONTEXT — Register SW + Load Blazor
    // ═══════════════════════════════════════════════════════════

    function loadBlazor() {
        if (document.querySelector('script[src*="blazor.webassembly"]')) return;
        const s = document.createElement('script');
        s.src = '_framework/blazor.webassembly.js';
        document.body.appendChild(s);
    }

    (async () => {
        if (window.crossOriginIsolated) {
            sessionStorage.removeItem('wt-sw-reload');
            loadBlazor();
            return;
        }

        if (!('serviceWorker' in navigator)) {
            console.warn('[WebTorrent SW] Service workers not supported');
            loadBlazor();
            return;
        }

        const reloadKey = 'wt-sw-reload';
        const reloadCount = parseInt(sessionStorage.getItem(reloadKey) || '0', 10);

        if (reloadCount >= 2) {
            console.warn('[WebTorrent SW] Cross-origin isolation failed — proceeding without SharedArrayBuffer');
            sessionStorage.removeItem(reloadKey);
            loadBlazor();
            return;
        }

        try {
            const reg = await navigator.serviceWorker.register(window.document.currentScript.src, { updateViaCache: 'none' });
            console.log('[WebTorrent SW] Registered:', reg.scope);
            await reg.update();
        } catch (err) {
            console.error('[WebTorrent SW] Registration failed:', err);
            loadBlazor();
            return;
        }

        // Wait for SW to be ready, then reload for COI headers
        let reloaded = false;
        const doReload = () => {
            if (reloaded) return;
            reloaded = true;
            sessionStorage.setItem(reloadKey, String(reloadCount + 1));
            window.location.reload();
        };

        navigator.serviceWorker.ready.then(doReload);
        setTimeout(() => {
            if (!reloaded && navigator.serviceWorker.controller) {
                doReload();
            } else if (!reloaded) {
                console.warn('[WebTorrent SW] Not ready after 5s — loading without COI');
                loadBlazor();
            }
        }, 5000);
    })();

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

        // Post to first client, get initial response via MessageChannel
        const mc = new MessageChannel();
        const [data, port] = await new Promise((resolve) => {
            mc.port1.onmessage = (evt) => resolve([evt.data, mc.port1]);
            allClients[0].postMessage({
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
