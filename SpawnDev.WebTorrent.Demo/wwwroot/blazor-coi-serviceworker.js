/*! coi-serviceworker - Cross-Origin Isolation via Service Worker */
/*
 * This service worker intercepts all responses and adds the required
 * Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers
 * to enable SharedArrayBuffer support in the browser.
 *
 * Works locally and on static hosts like GitHub Pages where you
 * cannot set server response headers.
 *
 * Registration: Add <script src="blazor-coi-serviceworker.js"></script> to index.html
 *
 * Two modes:
 *   1. If index.html has a static <script src="_framework/blazor.webassembly.js"> tag,
 *      Blazor always loads and COI is purely additive (SharedArrayBuffer support).
 *   2. If no static tag exists, this script dynamically loads Blazor after COI is confirmed,
 *      with a fallback to load without COI after timeout/max retries.
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
    // --- Running as a Service Worker ---
    var verbose = false;
    function consoleLog(...args) {
        if (!verbose) return;
        console.log("[COI]", ...args);
    }

    self.addEventListener("install", function () { self.skipWaiting(); });
    self.addEventListener("activate", function (event) { event.waitUntil(self.clients.claim()); });

    self.addEventListener("fetch", function (event) {
        // Skip requests the SW can't meaningfully intercept
        if (event.request.cache === "only-if-cached" && event.request.mode !== "same-origin") {
            return;
        }

        var url = new URL(event.request.url);

        // Don't intercept cross-origin requests
        if (url.origin !== self.location.origin) {
            return;
        }

        // ── WebTorrent streaming: intercept /webtorrent/ requests ──
        if (url.pathname.includes('/webtorrent/')) {
            event.respondWith(handleTorrentStream(event));
            return;
        }

        // ── COI headers for all other same-origin requests ──
        event.respondWith(
            fetch(event.request)
                .then(function (response) {
                    var newHeaders = new Headers(response.headers);
                    newHeaders.set("Cross-Origin-Embedder-Policy", "credentialless");
                    newHeaders.set("Cross-Origin-Opener-Policy", "same-origin");

                    return new Response(response.body, {
                        status: response.status,
                        statusText: response.statusText,
                        headers: newHeaders,
                    });
                })
                .catch(function (e) {
                    consoleLog("[COI] Fetch failed for:", event.request.url, e.message);
                    return new Response("Service Worker fetch failed", {
                        status: 502,
                        statusText: "Service Worker Fetch Failed",
                    });
                })
        );
    });

    // ── WebTorrent streaming via MessageChannel to main window ──
    async function handleTorrentStream(event) {
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
                    // Pull-based streaming — the client sends chunks on demand
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
