import http from "node:http";
import { createReadStream, existsSync, statSync } from "node:fs";
import { extname, resolve, sep } from "node:path";
import { pipeline } from "node:stream";

const port = Number(process.env.DELUNO_WEB_PORT ?? 5174);
const apiOrigin = new URL(process.env.DELUNO_API_ORIGIN ?? "http://127.0.0.1:5199");
// The local gate serves its own copy of the bundle (DELUNO_SMOKE_DIST) so a second build into dist cannot empty it.
const distRoot = process.env.DELUNO_SMOKE_DIST ? resolve(process.env.DELUNO_SMOKE_DIST) : resolve(process.cwd(), "dist");
const mimeTypes = {
  ".css": "text/css; charset=utf-8",
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".woff2": "font/woff2",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".webp": "image/webp"
};

function proxyApi(request, response) {
  const upstream = http.request({
    hostname: apiOrigin.hostname,
    port: apiOrigin.port || (apiOrigin.protocol === "https:" ? 443 : 80),
    protocol: apiOrigin.protocol,
    method: request.method,
    path: request.url,
    headers: { ...request.headers, host: apiOrigin.host }
  }, (upstreamResponse) => {
    response.writeHead(upstreamResponse.statusCode ?? 502, upstreamResponse.headers);
    pipeline(upstreamResponse, response, () => {});
  });

  upstream.on("error", () => {
    if (response.headersSent) {
      // Part of the API's answer already went out; cut it off rather than
      // appending an error body to it.
      response.destroy();
      return;
    }
    response.writeHead(502, { "content-type": "application/json" });
    response.end(JSON.stringify({ error: "The disposable smoke-test API is unavailable." }));
  });

  // A browser that goes away (a closed page, or a test's context torn down)
  // must take its API request with it. Otherwise a long-lived response -- the
  // hub's SSE stream above all -- stays open against the backend for the rest
  // of the run, one more for every page that ever opened one.
  response.on("close", () => {
    if (!response.writableFinished) {
      upstream.destroy();
    }
  });
  pipeline(request, upstream, () => {});
}

// Stands in for a reverse proxy that does not upgrade WebSockets (#161): the
// hub's WebSocket attempt must fail at once so SignalR falls back to SSE. The
// refusal has to be explicit. Since Node 24.9, an upgrade request with no
// 'upgrade' listener reaches the request handler, which forwarded it to the
// API; the API accepted it, the answer never came back, and every page's
// WebSocket hung open until the page closed instead of falling back.
function refuseUpgrade(request, socket) {
  socket.on("error", () => {});
  socket.end("HTTP/1.1 501 Not Implemented\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
}

function serveApp(request, response) {
  const pathname = decodeURIComponent(new URL(request.url ?? "/", "http://localhost").pathname);
  const requestedPath = resolve(distRoot, `.${pathname}`);
  const safeRequestedPath = requestedPath === distRoot || requestedPath.startsWith(`${distRoot}${sep}`);
  const filePath = safeRequestedPath && existsSync(requestedPath) && statSync(requestedPath).isFile()
    ? requestedPath
    : resolve(distRoot, "index.html");

  response.writeHead(200, {
    "content-type": mimeTypes[extname(filePath)] ?? "application/octet-stream",
    "cache-control": "no-store"
  });
  // pipeline, not pipe: a read that fails ends this one response instead of
  // throwing an unhandled 'error' that takes the whole server down.
  pipeline(createReadStream(filePath), response, () => {});
}

const server = http.createServer((request, response) => {
  const pathname = new URL(request.url ?? "/", "http://localhost").pathname;
  // /hubs must reach the API as well, for negotiation and the SSE stream.
  // WebSocket upgrades are refused (see refuseUpgrade), so the client
  // negotiates down to SSE -- which is exactly the fallback #161 restored.
  if (pathname.startsWith("/api/") || pathname.startsWith("/hubs")) {
    proxyApi(request, response);
    return;
  }

  serveApp(request, response);
});

server.on("upgrade", refuseUpgrade);

server.listen(port, "127.0.0.1", () => {
  console.log(`Smoke preview listening on http://127.0.0.1:${port}`);
});

for (const signal of ["SIGINT", "SIGTERM"]) {
  // close() alone waits for every open connection, and the hub's SSE streams
  // never end by themselves, so the server would stop accepting but not exit.
  process.on(signal, () => {
    server.close(() => process.exit(0));
    server.closeAllConnections();
  });
}
