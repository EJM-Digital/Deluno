/**
 * Deluno Service Worker — offline-capable, installable PWA.
 *
 * Strategy:
 *   · Shell assets (JS/CSS/fonts)  → Cache-first, background update
 *   · API calls (/api/*)           → Network-first, 5 s timeout → stale cache
 *   · Navigation requests          → Network-first → cached shell (offline SPA)
 *   · Images / media               → Cache-first, never stale
 *
 * Cache names are versioned so old caches are purged on SW activation.
 */

const VERSION = "v1";
const SHELL_CACHE = `deluno-shell-${VERSION}`;
const API_CACHE = `deluno-api-${VERSION}`;
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
  const currentCaches = [SHELL_CACHE, API_CACHE, IMAGE_CACHE];
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

  /* API calls → network-first with 5 s timeout, fallback to cache */
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/")) {
    if (request.method !== "GET") return; /* Don't cache mutations */
    event.respondWith(networkFirstWithTimeout(request, API_CACHE, 5000));
    return;
  }

  /* Images → cache-first */
  if (request.destination === "image") {
    event.respondWith(cacheFirst(request, IMAGE_CACHE));
    return;
  }

  /* Shell assets (JS, CSS, fonts, etc.) — cache-first, update in background */
  if (
    url.pathname.startsWith("/assets/") ||
    request.destination === "script" ||
    request.destination === "style" ||
    request.destination === "font"
  ) {
    event.respondWith(staleWhileRevalidate(request, SHELL_CACHE));
    return;
  }

  /* Navigation (HTML) → network first, fall back to shell index */
  if (request.mode === "navigate") {
    event.respondWith(navigationHandler(request));
    return;
  }
});

/* ── Strategies ──────────────────────────────────────────────────── */

async function networkFirstWithTimeout(request, cacheName, timeoutMs) {
  const cache = await caches.open(cacheName);
  const controller = new AbortController();
  const id = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const response = await fetch(request, { signal: controller.signal });
    clearTimeout(id);
    if (response.ok) {
      await cache.put(request, response.clone());
    }
    return response;
  } catch {
    clearTimeout(id);
    const cached = await cache.match(request);
    return cached ?? Response.error();
  }
}

async function cacheFirst(request, cacheName) {
  const cached = await caches.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  if (response.ok) {
    const cache = await caches.open(cacheName);
    await cache.put(request, response.clone());
  }
  return response;
}

async function staleWhileRevalidate(request, cacheName) {
  const cache = await caches.open(cacheName);
  const cached = await cache.match(request);

  const fetchPromise = fetch(request).then((response) => {
    if (response.ok) {
      cache.put(request, response.clone());
    }
    return response;
  });

  return cached ?? fetchPromise;
}

async function navigationHandler(request) {
  try {
    const response = await fetch(request);
    if (response.ok) {
      const cache = await caches.open(SHELL_CACHE);
      await cache.put(request, response.clone());
    }
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
