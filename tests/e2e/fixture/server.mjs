// The end-to-end suite's fixture (tests/e2e, docs/exec-plans/active/PLAN-E2E-IN-CI.md).
//
// What it does, with only Node's standard library and ffmpeg:
//
//   * makes, once at start, a small film that is a real video (ffmpeg's test pattern and a tone, encoded as Matroska) and a
//     tiny extra (a featurette);
//   * POST /films makes a release of its own from them (a folder named like a scene release: the film, a sample, an .nfo and,
//     when asked, a featurette), hashes it into a real .torrent and offers it on the Torznab feed;
//   * the .torrent carries no tracker. It names this server as a web seed (BEP 19, "url-list"), so a download client
//     given the torrent really downloads every byte of it from /webseed/ over HTTP, with no internet and no second client;
//   * GET /api?t=caps|search|movie|tvsearch is the Torznab feed;  GET /dl/<name>.torrent is the torrent;
//   * GET /inspect?root=media|downloads|weir&contains=<text> lists the files under a shared folder (read-only), and
//     GET /probe?root=...&path=... is ffprobe's reading of one, so a spec can say what is on disk, and that it is a video,
//     without trusting any application's word for it.
//
// Nothing here is the product: Deluno, the download client and Weir are the real published images.
import { createHash } from "node:crypto";
import { execFile } from "node:child_process";
import fs from "node:fs";
import http from "node:http";
import path from "node:path";

const PORT = Number(process.env.FIXTURE_PORT || "8765");
// The name other containers reach this one by; the web seed address inside every torrent.
const PUBLIC_BASE = process.env.FIXTURE_PUBLIC_BASE || `http://fixture:${PORT}`;
const WORK = process.env.FIXTURE_WORK || "/work";
const FILM_MB = Number(process.env.FIXTURE_FILM_MB || "24");
const ROOTS = { media: "/media", downloads: "/downloads", weir: "/weir" };
const PIECE = 256 * 1024;

const RELEASES = []; // newest last: title, torrent, infohash, size, cat, seeders

function log(msg) {
  console.log(new Date().toTimeString().slice(0, 8) + " " + msg);
}

// ------------------------------------------------------------------ bencode and the torrent

function bencode(value) {
  if (Buffer.isBuffer(value)) return Buffer.concat([Buffer.from(`${value.length}:`), value]);
  if (typeof value === "number") return Buffer.from(`i${value}e`);
  if (typeof value === "string") return bencode(Buffer.from(value, "utf8"));
  if (Array.isArray(value)) return Buffer.concat([Buffer.from("l"), ...value.map(bencode), Buffer.from("e")]);
  if (value && typeof value === "object") {
    const keys = Object.keys(value).map((k) => Buffer.from(k, "utf8")).sort(Buffer.compare);
    const parts = [Buffer.from("d")];
    for (const key of keys) parts.push(bencode(key), bencode(value[key.toString("utf8")]));
    parts.push(Buffer.from("e"));
    return Buffer.concat(parts);
  }
  throw new TypeError("cannot bencode " + typeof value);
}

function sha1(buf) {
  return createHash("sha1").update(buf).digest();
}

function walk(folder) {
  const out = [];
  for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
    const full = path.join(folder, entry.name);
    if (entry.isDirectory()) out.push(...walk(full));
    else out.push(full);
  }
  return out;
}

// A multi-file torrent over every file under folder (path order), with a web seed and no tracker.
function makeTorrent(folder, name, webSeedBase) {
  const files = walk(folder).map((f) => path.relative(folder, f).split(path.sep).join("/"));
  files.sort((a, b) => Buffer.compare(Buffer.from(a), Buffer.from(b)));
  const entries = [];
  const pieces = [];
  let buf = Buffer.alloc(0);
  let total = 0;
  for (const rel of files) {
    const data = fs.readFileSync(path.join(folder, rel));
    entries.push({ length: data.length, path: rel.split("/") });
    total += data.length;
    buf = buf.length ? Buffer.concat([buf, data]) : data;
    while (buf.length >= PIECE) {
      pieces.push(sha1(buf.subarray(0, PIECE)));
      buf = buf.subarray(PIECE);
    }
  }
  if (buf.length) pieces.push(sha1(buf));
  const info = { name, "piece length": PIECE, pieces: Buffer.concat(pieces), files: entries };
  const infohash = sha1(bencode(info)).toString("hex");
  const torrent = bencode({ info, "url-list": [webSeedBase], comment: "Deluno end-to-end fixture" });
  return { torrent, infohash, total, files };
}

// ------------------------------------------------------------------ the films

function run(file, args) {
  return new Promise((resolve, reject) => {
    execFile(file, args, { maxBuffer: 64 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error) reject(Object.assign(new Error(`${file} failed: ${String(stderr).slice(-400)}`), { stdout, stderr }));
      else resolve({ stdout, stderr });
    });
  });
}

const ffmpeg = (args) => run("ffmpeg", ["-y", "-v", "error", ...args]);

async function makeBaseVideos() {
  fs.mkdirSync(path.join(WORK, "base"), { recursive: true });
  const film = path.join(WORK, "base", "film.mkv");
  const extra = path.join(WORK, "base", "extra.mkv");
  if (!fs.existsSync(film)) {
    // A real, playable film: 640x360 test pattern with a tone. The bitrate is set so the file is about FILM_MB large.
    const seconds = 130; // Deluno treats a film under two minutes as a likely sample
    const rate = Math.trunc((FILM_MB * 8 * 1024) / seconds);
    await ffmpeg(["-f", "lavfi", "-i", `testsrc2=size=640x360:rate=24:duration=${seconds}`,
      "-f", "lavfi", "-i", `sine=frequency=440:sample_rate=48000:duration=${seconds}`,
      "-c:v", "mpeg4", "-b:v", `${rate}k`, "-minrate", `${rate}k`, "-maxrate", `${rate}k`,
      "-bufsize", `${rate}k`, "-c:a", "aac", "-b:a", "96k", "-shortest", film]);
  }
  if (!fs.existsSync(extra)) {
    await ffmpeg(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=4",
      "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:duration=4",
      "-c:v", "mpeg4", "-b:v", "200k", "-c:a", "aac", "-b:a", "48k", "-shortest", extra]);
  }
  return [film, extra];
}

async function newFilm(title, year, withExtra) {
  const release = `${title}.${year}.1080p.WEB-DL.x264-E2E`;
  const folder = path.join(WORK, "films", release);
  if (fs.existsSync(folder) && fs.statSync(folder).isDirectory()) throw new Error("a release of that name already exists");
  const [baseFilm, baseExtra] = await makeBaseVideos();
  fs.mkdirSync(path.join(folder, "Sample"), { recursive: true });
  // Each release is its own bytes (a stream copy with the release's name as the file's title), so no two share a piece.
  await ffmpeg(["-i", baseFilm, "-c", "copy", "-metadata", `title=${release}`, path.join(folder, `${release}.mkv`)]);
  fs.copyFileSync(baseExtra, path.join(folder, "Sample", "sample.mkv"));
  fs.writeFileSync(path.join(folder, `${release}.nfo`), "end-to-end fixture release\n");
  if (withExtra) {
    fs.mkdirSync(path.join(folder, "Featurettes"), { recursive: true });
    fs.copyFileSync(baseExtra, path.join(folder, "Featurettes", "featurette.mkv"));
  }
  const { torrent, infohash, total, files } = makeTorrent(folder, release, `${PUBLIC_BASE}/webseed/`);
  fs.mkdirSync(path.join(WORK, "torrents"), { recursive: true });
  const name = `${release}.torrent`;
  fs.writeFileSync(path.join(WORK, "torrents", name), torrent);
  RELEASES.push({ title: release, torrent: name, infohash, size: total, cat: 2000, seeders: 50 });
  log(`made ${release}: ${total} bytes, ${files.length} files, hash ${infohash}`);
  return {
    title, year, release, infohash, size: total, url: `${PUBLIC_BASE}/dl/${name}`,
    files: files.map((f) => ({ path: f, size: fs.statSync(path.join(folder, f)).size })),
  };
}

// ------------------------------------------------------------------ the feed

const CAPS = '<?xml version="1.0" encoding="UTF-8"?><caps><server version="1.0" title="E2E feed" />' +
  '<limits max="100" default="100"/><searching><search available="yes" supportedParams="q"/>' +
  '<tv-search available="yes" supportedParams="q,season,ep,tvdbid"/>' +
  '<movie-search available="yes" supportedParams="q,imdbid,tmdbid"/></searching>' +
  '<categories><category id="2000" name="Movies"><subcat id="2040" name="Movies/HD"/><subcat id="2045" name="Movies/UHD"/></category>' +
  '<category id="5000" name="TV"><subcat id="5040" name="TV/HD"/><subcat id="5045" name="TV/UHD"/></category></categories></caps>';

const esc = (text) => text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

function searchXml(query) {
  const words = (query.get("q") || "").toLowerCase().split(/[ .\-_()]/).filter((w) => w.length >= 3);
  const kind = (query.get("t") || "").toLowerCase();
  const items = [];
  for (const row of [...RELEASES]) {
    if (kind === "tvsearch" || kind === "tv") continue;
    if (words.length && !words.some((w) => row.title.toLowerCase().includes(w))) continue;
    const url = `${PUBLIC_BASE}/dl/${row.torrent}`;
    items.push(
      `<item><title>${esc(row.title)}</title><guid isPermaLink="false">${row.infohash}</guid><link>${url}</link>` +
      `<pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><size>${row.size}</size>` +
      `<enclosure url="${url}" length="${row.size}" type="application/x-bittorrent"/>` +
      `<torznab:attr name="category" value="${row.cat}"/><torznab:attr name="seeders" value="${row.seeders}"/>` +
      `<torznab:attr name="peers" value="${row.seeders + 2}"/><torznab:attr name="infohash" value="${row.infohash}"/>` +
      `<torznab:attr name="size" value="${row.size}"/></item>`);
  }
  return '<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" ' +
    'xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><title>E2E feed</title><description>local</description>' +
    `<link>${PUBLIC_BASE}/</link><torznab:response offset="0" total="${items.length}"/>` + items.join("") + "</channel></rss>";
}

// ------------------------------------------------------------------ http

function sendBytes(req, res, status, body, ctype = "application/xml", extra = {}) {
  const data = Buffer.isBuffer(body) ? body : Buffer.from(body);
  res.writeHead(status, { "Content-Type": ctype, "Content-Length": data.length, "Accept-Ranges": "bytes", ...extra });
  res.end(req.method === "HEAD" ? undefined : data);
}

function sendFile(req, res, file) {
  const size = fs.statSync(file).size;
  let start = 0;
  let end = size - 1;
  let status = 200;
  const header = req.headers.range;
  if (header) {
    const match = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
    if (match) {
      if (match[1] === "" && match[2]) {
        start = Math.max(0, size - Number(match[2]));
      } else {
        start = Number(match[1] || 0);
        if (match[2]) end = Math.min(size - 1, Number(match[2]));
      }
      if (start > end || start >= size) {
        sendBytes(req, res, 416, "", "text/plain", { "Content-Range": `bytes */${size}` });
        return;
      }
      status = 206;
    }
  }
  const length = end - start + 1;
  const headers = { "Content-Type": "application/octet-stream", "Content-Length": length, "Accept-Ranges": "bytes" };
  if (status === 206) headers["Content-Range"] = `bytes ${start}-${end}/${size}`;
  res.writeHead(status, headers);
  if (req.method === "HEAD" || length === 0) {
    res.end();
    return;
  }
  const stream = fs.createReadStream(file, { start, end });
  stream.on("error", () => res.destroy());
  res.on("close", () => stream.destroy());
  stream.pipe(res);
}

async function handleGet(req, res, url) {
  const route = decodeURIComponent(url.pathname);
  const query = url.searchParams;
  if (route === "/health") return sendBytes(req, res, 200, "ok", "text/plain");
  if (route === "/api") {
    const kind = (query.get("t") || "").toLowerCase();
    return sendBytes(req, res, 200, kind === "caps" ? CAPS : searchXml(query));
  }
  if (route.startsWith("/dl/")) {
    const target = path.join(WORK, "torrents", path.basename(route));
    if (fs.existsSync(target)) return sendBytes(req, res, 200, fs.readFileSync(target), "application/x-bittorrent");
    return sendBytes(req, res, 404, "no", "text/plain");
  }
  if (route.startsWith("/webseed/")) {
    const rel = path.posix.normalize(route.slice("/webseed/".length));
    const target = path.join(WORK, "films", rel);
    if (rel.startsWith("..") || !fs.existsSync(target) || !fs.statSync(target).isFile()) return sendBytes(req, res, 404, "no", "text/plain");
    return sendFile(req, res, target);
  }
  if (route === "/inspect") {
    const root = ROOTS[query.get("root") || ""];
    const needle = (query.get("contains") || "").toLowerCase();
    const found = [];
    if (root && fs.existsSync(root) && fs.statSync(root).isDirectory()) {
      for (const full of walk(root)) {
        if (!full.toLowerCase().includes(needle)) continue;
        try {
          found.push({ path: full, size: fs.statSync(full).size });
        } catch { /* gone while listing */ }
      }
    }
    found.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
    return sendBytes(req, res, 200, JSON.stringify(found), "application/json");
  }
  if (route === "/probe") {
    const root = ROOTS[query.get("root") || ""];
    const target = path.posix.normalize(query.get("path") || "");
    if (!root || !target.startsWith(root + "/") || !fs.existsSync(target) || !fs.statSync(target).isFile()) return sendBytes(req, res, 404, "no", "text/plain");
    // What a player would find in the file: ffprobe's own reading of it.
    try {
      const { stdout } = await run("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", target]);
      return sendBytes(req, res, 200, stdout, "application/json");
    } catch (error) {
      return sendBytes(req, res, 422, error.stdout || JSON.stringify({ error: String(error.stderr || error.message) }), "application/json");
    }
  }
  return sendBytes(req, res, 404, "no", "text/plain");
}

async function handlePost(req, res, url) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  try {
    const raw = Buffer.concat(chunks).toString("utf8");
    const body = raw.trim() ? JSON.parse(raw) : {};
    if (url.pathname === "/films") {
      const made = await newFilm(body.title, Number.parseInt(body.year ?? 2020, 10), Boolean(body.extra));
      return sendBytes(req, res, 200, JSON.stringify(made), "application/json");
    }
    return sendBytes(req, res, 404, "no", "text/plain");
  } catch (error) { // the spec reads the message
    log("error: " + error.message);
    return sendBytes(req, res, 500, JSON.stringify({ error: String(error.message) }), "application/json");
  }
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, "http://localhost");
  log(`${req.socket.remoteAddress} "${req.method} ${req.url}"`);
  const work = req.method === "POST" ? handlePost(req, res, url)
    : req.method === "GET" || req.method === "HEAD" ? handleGet(req, res, url)
      : sendBytes(req, res, 501, "no", "text/plain");
  Promise.resolve(work).catch(() => res.destroy());
});
server.requestTimeout = 0;

fs.mkdirSync(WORK, { recursive: true });
await makeBaseVideos();
log(`base film ready; serving on ${PORT}, web seed ${PUBLIC_BASE}/webseed/`);
server.listen(PORT, "0.0.0.0");
