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
        var s = document.createElement('script');
        s.src = '_framework/blazor.webassembly.js';
        document.body.appendChild(s);
    }

    if (window.crossOriginIsolated) {
        sessionStorage.removeItem('wt-sw-reload');
        loadBlazor();
    } else if ('serviceWorker' in navigator) {
        var reloadKey = 'wt-sw-reload';
        var reloadCount = parseInt(sessionStorage.getItem(reloadKey) || '0', 10);

        if (reloadCount < 2) {
            navigator.serviceWorker
                .register(window.document.currentScript.src)
                .then(function (reg) {
                    console.log('[WebTorrent SW] Registered:', reg.scope);
                })
                .catch(function (err) {
                    console.error('[WebTorrent SW] Registration failed:', err);
                    loadBlazor();
                });

            var reloaded = false;
            var doReload = function () {
                if (reloaded) return;
                reloaded = true;
                sessionStorage.setItem(reloadKey, String(reloadCount + 1));
                window.location.reload();
            };

            navigator.serviceWorker.ready.then(doReload);
            setTimeout(function () {
                if (!reloaded && navigator.serviceWorker.controller) {
                    doReload();
                } else if (!reloaded) {
                    console.warn('[WebTorrent SW] Not ready after 5s — loading without COI');
                    loadBlazor();
                }
            }, 5000);
        } else {
            console.warn('[WebTorrent SW] Cross-origin isolation failed — proceeding without SharedArrayBuffer');
            sessionStorage.removeItem(reloadKey);
            loadBlazor();
        }
    } else {
        console.warn('[WebTorrent SW] Service workers not supported');
        loadBlazor();
    }

} else {
    // ═══════════════════════════════════════════════════════════
    //  SERVICE WORKER CONTEXT — COI Headers + Torrent Streaming
    // ═══════════════════════════════════════════════════════════

    self.addEventListener('install', function () { self.skipWaiting(); });
    self.addEventListener('activate', function (event) { event.waitUntil(self.clients.claim()); });

    self.addEventListener('fetch', function (event) {
        if (event.request.cache === 'only-if-cached' && event.request.mode !== 'same-origin') {
            return;
        }

        var url = new URL(event.request.url);
        if (url.origin !== self.location.origin) {
            return;
        }

        // WebTorrent streaming
        if (url.pathname.includes('/webtorrent/')) {
            event.respondWith(handleWebtorrentStream(event));
            return;
        }

        // COI headers for all other same-origin requests
        event.respondWith(
            fetch(event.request)
                .then(function (response) {
                    var headers = new Headers(response.headers);
                    headers.set('Cross-Origin-Embedder-Policy', 'credentialless');
                    headers.set('Cross-Origin-Opener-Policy', 'same-origin');
                    return new Response(response.body, {
                        status: response.status,
                        statusText: response.statusText,
                        headers: headers,
                    });
                })
                .catch(function (e) {
                    return new Response('Service Worker fetch failed', { status: 502 });
                })
        );
    });

    // ── WebTorrent streaming ──
    // Matches the protocol from webtorrent/webtorrent (worker-server.js):
    // 1. SW posts request details to client window via MessageChannel
    // 2. Client responds with { status, headers, body } where body is 'STREAM' or data
    // 3. If 'STREAM': SW creates ReadableStream, pulls chunks via port messages
    // 4. Client sends Uint8Array chunks on pull (true), null = end, false = cancel

    async function handleWebtorrentStream(event) {
        var allClients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        if (allClients.length === 0) {
            return new Response('No client available', { status: 503 });
        }

        var request = event.request;
        var url = new URL(request.url);

        // Race: post to all clients, first to respond wins (matches webtorrent pattern)
        var result = await new Promise(function (resolve) {
            for (var i = 0; i < allClients.length; i++) {
                var mc = new MessageChannel();
                mc.port1.onmessage = function (evt) {
                    resolve([evt.data, mc.port1]);
                };
                allClients[i].postMessage({
                    type: 'webtorrent',
                    url: request.url,
                    method: request.method,
                    headers: Object.fromEntries(request.headers.entries()),
                    scope: self.registration.scope,
                    destination: request.destination,
                }, [mc.port2]);
                // Only use first client's channel for the resolve
                var mc = { port1: mc.port1 };
            }
        });

        var data = result[0];
        var port = result[1];

        if (!data) {
            return new Response('No response from client', { status: 500 });
        }

        if (data.body !== 'STREAM') {
            // Direct response (small files, errors, etc.)
            port.onmessage = null;
            return new Response(data.body, {
                status: data.status || 200,
                headers: data.headers || {},
            });
        }

        // Streaming response — pull chunks from client on demand
        var timeOut = null;
        var portTimeoutDuration = 5000;
        var cleanup = function () {
            port.postMessage(false); // cancel
            clearTimeout(timeOut);
            port.onmessage = null;
        };

        var stream = new ReadableStream({
            pull: function (controller) {
                return new Promise(function (resolve) {
                    port.onmessage = function (msg) {
                        if (msg.data) {
                            controller.enqueue(msg.data); // Uint8Array chunk
                        } else {
                            cleanup();
                            controller.close();
                        }
                        resolve();
                    };
                    // Timeout for non-document requests (Firefox compat)
                    clearTimeout(timeOut);
                    if (data.destination !== 'document') {
                        timeOut = setTimeout(function () {
                            cleanup();
                            resolve();
                        }, portTimeoutDuration);
                    }
                    port.postMessage(true); // pull request
                });
            },
            cancel: function () {
                cleanup();
            }
        });

        return new Response(stream, {
            status: data.status || 200,
            headers: data.headers || {},
        });
    }
}
