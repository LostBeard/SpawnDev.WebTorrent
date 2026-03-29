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
        // Already cross-origin isolated — SW is active, COI headers applied
        sessionStorage.removeItem('wt-sw-reload');
        loadBlazor();
    } else if ('serviceWorker' in navigator) {
        var reloadKey = 'wt-sw-reload';
        var reloadCount = parseInt(sessionStorage.getItem(reloadKey) || '0', 10);

        if (reloadCount < 2) {
            // Register this file as the service worker
            navigator.serviceWorker
                .register(window.document.currentScript.src)
                .then(function (reg) {
                    console.log('[WebTorrent SW] Registered:', reg.scope);
                })
                .catch(function (err) {
                    console.error('[WebTorrent SW] Registration failed:', err);
                    loadBlazor();
                });

            // Wait for SW to be ready, then reload to pick up COI headers
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
            // Already tried — COI not working, load Blazor without it
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

        // Don't intercept cross-origin requests
        if (url.origin !== self.location.origin) {
            return;
        }

        // WebTorrent streaming — intercept /webtorrent/ requests
        if (url.pathname.includes('/webtorrent/')) {
            event.respondWith(handleWebtorrentStream(event));
            return;
        }

        // All other same-origin requests — add COI headers
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

    // ── WebTorrent streaming via MessageChannel ──

    async function handleWebtorrentStream(event) {
        var allClients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        if (allClients.length === 0) {
            return new Response('No client available', { status: 503 });
        }

        var client = allClients[0];
        var mc = new MessageChannel();
        var url = new URL(event.request.url);
        var rangeHeader = event.request.headers.get('range');

        return new Promise(function (resolve) {
            mc.port1.onmessage = function (evt) {
                var data = evt.data;

                if (!data || data.error) {
                    resolve(new Response(data ? data.error : 'No response', { status: 500 }));
                    return;
                }

                if (data.body === 'stream_pull') {
                    // Pull-based streaming — client sends chunks on demand
                    var stream = new ReadableStream({
                        pull: function (controller) {
                            return new Promise(function (pullResolve) {
                                mc.port1.onmessage = function (chunkEvt) {
                                    if (chunkEvt.data) {
                                        try {
                                            controller.enqueue(chunkEvt.data);
                                        } catch (ex) {
                                            mc.port1.postMessage({ eventType: 'error', desiredSize: 0 });
                                        }
                                    } else {
                                        try { controller.close(); } catch (e) { }
                                        mc.port1.onmessage = null;
                                    }
                                    pullResolve();
                                };
                                mc.port1.postMessage({ eventType: 'pull', desiredSize: controller.desiredSize });
                            });
                        },
                        cancel: function () {
                            mc.port1.postMessage({ eventType: 'cancel', desiredSize: 0 });
                        }
                    });
                    resolve(new Response(stream, {
                        status: data.status || 200,
                        headers: data.headers || {}
                    }));
                } else {
                    // Direct response — complete data in one message
                    resolve(new Response(data.body, {
                        status: data.status || 200,
                        headers: data.headers || {}
                    }));
                }
            };

            client.postMessage({
                type: 'webtorrent-stream',
                url: url.pathname,
                range: rangeHeader,
            }, [mc.port2]);
        });
    }
}
