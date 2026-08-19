import http from "node:http";
import { createReadStream, existsSync, statSync } from "node:fs";
import { extname, resolve, sep } from "node:path";

const port = Number(process.env.DELUNO_WEB_PORT ?? 5174);
const apiOrigin = new URL(process.env.DELUNO_API_ORIGIN ?? "http://127.0.0.1:5199");
const distRoot = resolve(process.cwd(), "dist");
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
    upstreamResponse.pipe(response);
  });

  upstream.on("error", () => {
    if (!response.headersSent) {
      response.writeHead(502, { "content-type": "application/json" });
    }
    response.end(JSON.stringify({ error: "The disposable smoke-test API is unavailable." }));
  });
  request.pipe(upstream);
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
  createReadStream(filePath).pipe(response);
}

const server = http.createServer((request, response) => {
  const pathname = new URL(request.url ?? "/", "http://localhost").pathname;
  // /hubs must reach the API as well. This server does not proxy WebSocket
  // upgrades, so the client negotiates down to SSE -- which is exactly the
  // fallback this change exists to restore.
  if (pathname.startsWith("/api/") || pathname.startsWith("/hubs")) {
    proxyApi(request, response);
    return;
  }

  serveApp(request, response);
});

server.listen(port, "127.0.0.1", () => {
  console.log(`Smoke preview listening on http://127.0.0.1:${port}`);
});

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
