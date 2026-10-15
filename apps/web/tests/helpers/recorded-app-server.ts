import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";

// Another app, answering over a real loopback port with the replies that app really gave (tests/fixtures/recorded).
// The browser tests that need a download client or a search source use this, so the app under test talks to something that
// answers the way qBittorrent or Jackett really answered, never to a reply written by hand. A call no recording answers
// gets a 599 and is listed in `unanswered`, so a spec that sends something nobody recorded fails and says what to record.

const here = path.dirname(fileURLToPath(import.meta.url));
const recordedRoot = path.resolve(here, "../../../../tests/fixtures/recorded");

interface Exchange {
  request: { method: string; path: string };
  reply: { status: number; headers?: Record<string, string>; body?: unknown; bodyText?: string };
}

interface Recording {
  app: string;
  version: string;
  state: string;
  exchanges: Exchange[];
}

/** `qbittorrent/5.2.1/version`: the app, the version the recording was made against, and the state. */
export function loadRecording(app: string, version: string, state: string): Recording {
  return JSON.parse(fs.readFileSync(path.join(recordedRoot, app, version, `${state}.json`), "utf8")) as Recording;
}

export interface RecordedAppServer {
  readonly host: string;
  readonly port: number;
  readonly baseUrl: string;
  /** Every request the app received, in order: method and path with its query. */
  readonly requests: string[];
  /** Requests no recording answers. */
  readonly unanswered: string[];
  close(): Promise<void>;
}

function parts(target: string) {
  const url = new URL(target, "http://recorded.invalid");
  return { pathname: url.pathname, query: url.searchParams };
}

/** Starts a server that answers each call with the recorded reply to the same kind of call. */
/** `delayMs` holds every answer back, so a screen's "Testing..." state can be seen before the answer comes. */
export async function startRecordedAppServer(recordings: Recording[], options: { delayMs?: number } = {}): Promise<RecordedAppServer> {
  const exchanges = recordings.flatMap((recording) => recording.exchanges);
  const requests: string[] = [];
  const unanswered: string[] = [];

  const server = http.createServer((incoming, outgoing) => {
    const chunks: Buffer[] = [];
    incoming.on("data", (chunk: Buffer) => chunks.push(chunk));
    incoming.on("end", async () => {
      if (options.delayMs) await new Promise((resolve) => setTimeout(resolve, options.delayMs)); // reason: the delay is the point, so the screen's Testing state can be seen
      const asked = parts(incoming.url ?? "/");
      requests.push(`${incoming.method} ${incoming.url}`);
      // The same path and method; where the call names a Torznab function (t=) or a SABnzbd mode, that has to agree too.
      const found = exchanges.find((exchange) => {
        const recorded = parts(exchange.request.path);
        if (exchange.request.method !== incoming.method || recorded.pathname !== asked.pathname) return false;
        for (const key of ["t", "mode", "name"]) {
          if (recorded.query.get(key) !== asked.query.get(key) && (recorded.query.has(key) || asked.query.has(key))) return false;
        }
        return true;
      });
      if (!found) {
        unanswered.push(`${incoming.method} ${incoming.url}`);
        outgoing.writeHead(599, { "content-type": "text/plain" });
        outgoing.end("No recording answers this call.");
        return;
      }
      const text = found.reply.bodyText ?? (found.reply.body === undefined ? "" : JSON.stringify(found.reply.body));
      outgoing.writeHead(found.reply.status, found.reply.headers ?? {});
      outgoing.end(text);
    });
  });

  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return {
    host: "127.0.0.1",
    port,
    baseUrl: `http://127.0.0.1:${port}`,
    requests,
    unanswered,
    close: () => new Promise<void>((resolve) => { server.close(() => resolve()); server.closeAllConnections?.(); })
  };
}
