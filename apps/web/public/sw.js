/**
 * Deluno Service Worker — offline-capable, installable PWA.
 *
 * Strategy:
 *   · Shell assets (JS/CSS/fonts)  → Cache-first, background update
 *   · API calls (/api/*, /hubs/*)  → Not intercepted: they go straight to the
 *                                    network. The API is no-store and the app has
 *                                    its own timeouts, so copying every response
 *                                    body into Cache Storage (and aborting at 5 s)
 *                                    only slowed every screen down (#1081).
 *   · Navigation requests          → Network-first → cached shell (offline SPA)
 *   · Images / media               → Cache-first, never stale
 *
 * Cache writes never sit between the network and the page: the response is
 * returned at once and copied into the cache afterwards (event.waitUntil).
 * Cache names are versioned so old caches are purged on SW activation.
 */

const VERSION = "v2";
const SHELL_CACHE = `deluno-shell-${VERSION}`;
const IMAGE_CACHE = `deluno-img-${VERSION}`;

const PRECACHE_URLS = [
  "/",
  "/manifest.json",
  "/offline.html"
];

/* ── Install ─────────────────────────────────────────────────────── */
self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(SHELL_CACHE)
      .then((cache) => cache.addAll(PRECACHE_URLS))
      .then(() => self.skipWaiting())
  );
});

/* ── Activate ────────────────────────────────────────────────────── */
self.addEventListener("activate", (event) => {
  const currentCaches = [SHELL_CACHE, IMAGE_CACHE];
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter((key) => !currentCaches.includes(key))
            .map((key) => caches.delete(key))
        )
      )
      .then(() => self.clients.claim())
  );
});

/* ── Fetch ───────────────────────────────────────────────────────── */
self.addEventListener("fetch", (event) => {
  const { request } = event;
  const url = new URL(request.url);

  /* WebSocket — never intercept */
  if (url.protocol === "ws:" || url.protocol === "wss:") return;

  /* API and SignalR traffic: straight to the network, no cache copy, no
     worker-imposed timeout. */
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/")) {
    return;
  }

  /* Images → cache-first */
  if (request.destination === "image") {
    event.respondWith(cacheFirst(event, request, IMAGE_CACHE));
    return;
  }

  /* Shell assets (JS, CSS, fonts, etc.) — cache-first, update in background */
  if (
    url.pathname.startsWith("/assets/") ||
    request.destination === "script" ||
    request.destination === "style" ||
    request.destination === "font"
  ) {
    event.respondWith(staleWhileRevalidate(event, request, SHELL_CACHE));
    return;
  }

  /* Navigation (HTML) → network first, fall back to shell index */
  if (request.mode === "navigate") {
    event.respondWith(navigationHandler(event, request));
    return;
  }
});

/* ── Strategies ──────────────────────────────────────────────────── */

/* Copy a response into the cache after it has been handed to the page. */
function cacheInBackground(event, cacheName, request, response) {
  if (!response.ok) return;
  const copy = response.clone();
  event.waitUntil(
    caches
      .open(cacheName)
      .then((cache) => cache.put(request, copy))
      .catch(() => {})
  );
}

async function cacheFirst(event, request, cacheName) {
  const cached = await caches.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  cacheInBackground(event, cacheName, request, response);
  return response;
}

async function staleWhileRevalidate(event, request, cacheName) {
  const cache = await caches.open(cacheName);
  const cached = await cache.match(request);

  const fetchPromise = fetch(request).then((response) => {
    cacheInBackground(event, cacheName, request, response);
    return response;
  });

  if (cached) {
    event.waitUntil(fetchPromise.catch(() => {}));
    return cached;
  }
  return fetchPromise;
}

async function navigationHandler(event, request) {
  try {
    const response = await fetch(request);
    cacheInBackground(event, SHELL_CACHE, request, response);
    return response;
  } catch {
    /* Offline: serve the cached shell or the offline page */
    const cached = await caches.match(request);
    if (cached) return cached;
    const shell = await caches.match("/");
    if (shell) return shell;
    const offline = await caches.match("/offline.html");
    return offline ?? Response.error();
  }
}

/* ── Push notifications (future-proof stub) ──────────────────────── */
self.addEventListener("push", (event) => {
  if (!event.data) return;
  const data = event.data.json();
  event.waitUntil(
    self.registration.showNotification(data.title ?? "Deluno", {
      body: data.body ?? "",
      icon: "/icon-192.png",
      badge: "/icon-192.png",
      tag: data.tag ?? "deluno",
      data: { url: data.url ?? "/" }
    })
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = event.notification.data?.url ?? "/";
  event.waitUntil(
    self.clients
      .matchAll({ type: "window", includeUncontrolled: true })
      .then((clients) => {
        for (const client of clients) {
          if (client.url === url && "focus" in client) return client.focus();
        }
        if (self.clients.openWindow) return self.clients.openWindow(url);
      })
  );
});
