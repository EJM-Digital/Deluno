import http from "node:http";
import { appManifest } from "../../../packages/platform/src/app-manifest.js";
import { getMovieDomainSummary } from "../../../packages/movies/src/domain-summary.js";
import { getSeriesDomainSummary } from "../../../packages/series/src/domain-summary.js";
import { getProviderStrategy } from "../../../packages/integrations/src/provider-strategy.js";

const host = "127.0.0.1";
const port = Number(process.env.PORT || 4000);

function sendJson(response, statusCode, payload) {
  response.writeHead(statusCode, {
    "Content-Type": "application/json; charset=utf-8"
  });
  response.end(JSON.stringify(payload, null, 2));
}

function routeRequest(request, response) {
  const url = new URL(request.url || "/", `http://${request.headers.host || `${host}:${port}`}`);

  if (request.method === "GET" && url.pathname === "/health") {
    return sendJson(response, 200, {
      ok: true,
      service: "deluno-api",
      timestamp: new Date().toISOString()
    });
  }

  if (request.method === "GET" && url.pathname === "/api") {
    return sendJson(response, 200, {
      app: appManifest,
      routes: [
        "/health",
        "/api",
        "/api/domains",
        "/api/providers"
      ]
    });
  }

  if (request.method === "GET" && url.pathname === "/api/domains") {
    return sendJson(response, 200, {
      movies: getMovieDomainSummary(),
      series: getSeriesDomainSummary()
    });
  }

  if (request.method === "GET" && url.pathname === "/api/providers") {
    return sendJson(response, 200, getProviderStrategy());
  }

  return sendJson(response, 404, {
    error: "Not found",
    path: url.pathname
  });
}

const server = http.createServer(routeRequest);

server.listen(port, host, () => {
  console.log(`Deluno API listening on http://${host}:${port}`);
});
