// The end-to-end suite's fixture (tests/e2e, docs/exec-plans/active/PLAN-E2E-IN-CI.md).
//
// What it does, with only Node's standard library and ffmpeg:
//
//   * makes, once at start, a small film that is a real video (ffmpeg's test pattern and a tone, encoded as Matroska) and a
//     tiny extra (a featurette);
//   * POST /films with {"layout": "file"} makes a single-file torrent instead (the film alone, no folder);
//   * POST /films makes a release of its own from them (a folder named like a scene release: the film, a sample, an .nfo and,
//     when asked, a featurette), hashes it into a real .torrent and offers it on the Torznab feed;
//   * the .torrent carries no tracker. It names this server as a web seed (BEP 19, "url-list"), so a download client
//     given the torrent really downloads every byte of it from /webseed/ over HTTP, with no internet and no second client;
//   * GET /api?t=caps|search|movie|tvsearch is the Torznab feed;  GET /dl/<name>.torrent is the torrent;
//   * POST /films with {"usenet": true} makes a Usenet release instead: the film and an .nfo posted as real yEnc articles
//     (the way a newsgroup carries them) with the .nzb that lists them. GET /newznab/api is the Newznab feed offering them,
//     GET /dl/<name>.nzb is the .nzb, and an NNTP server on port 1119 (any login) serves the articles, so SABnzbd and NZBGet
//     download every byte over NNTP with no internet and no account;
//   * GET /inspect?root=media|downloads|weir&contains=<text> lists the files under a shared folder (read-only), and
//     GET /probe?root=...&path=... is ffprobe's reading of one, so a spec can say what is on disk, and that it is a video,
//     without trusting any application's word for it.
//
// Nothing here is the product: Deluno, the download client and Weir are the real published images.
import { createHash, randomBytes } from "node:crypto";
import { execFile } from "node:child_process";
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import path from "node:path";

const PORT = Number(process.env.FIXTURE_PORT || "8765");
// The name other containers reach this one by; the web seed address inside every torrent.
const PUBLIC_BASE = process.env.FIXTURE_PUBLIC_BASE || `http://fixture:${PORT}`;
const WORK = process.env.FIXTURE_WORK || "/work";
const FILM_MB = Number(process.env.FIXTURE_FILM_MB || "24");
const ROOTS = { media: "/media", downloads: "/downloads", weir: "/weir", pack: "/pack" };
const PIECE = 256 * 1024;
const NNTP_PORT = Number(process.env.FIXTURE_NNTP_PORT || "1119");
const PART_BYTES = 700 * 1024; // the data in one posted article
const GROUP = "alt.binaries.e2e";
const ARTICLES = new Map(); // message-id (no brackets) -> { head, body }

// What the stuck-download scenarios need (docs/golden-path.md, group stuck): a release that is never answered, or answered slowly.
//   SHORT_FILES - web-seed files served up to the start of the last piece, then left open: a torrent that stops just short of the end;
//   HUNG        - news articles the news server never answers (a Usenet release that makes no progress);
//   SLOWED      - news articles answered only after SLOW_MS (a Usenet release that is too slow).
const SHORT_FILES = new Set();
const HUNG = new Set();
const SLOWED = new Set();
const SLOW_MS = Number(process.env.FIXTURE_SLOW_MS || "15000");

const RELEASES = []; // newest last: title, torrent (or nzb), infohash, size, cat, seeders, usenet

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

// A second, bigger video for the season-pack group: the film played twice over (a stream copy), so it differs from the film in size and bytes.
async function bigVideo() {
  const [film] = await makeBaseVideos();
  const big = path.join(WORK, "base", "big.mkv");
  if (!fs.existsSync(big)) await ffmpeg(["-stream_loop", "1", "-i", film, "-c", "copy", big]);
  return big;
}

// Puts folders and copies of the base videos on the "pack" volume, owned by Deluno's user (1654), for the groups that import from a
// folder (seasonpack). dirs: folders to make; copies: [{ to, from: "small" | "big" }] where small is the film.
async function stage(body) {
  const owner = 1654;
  const made = [];
  for (const dir of body.dirs ?? []) {
    if (!dir.startsWith("/pack/") || dir.includes("..")) throw new Error("only folders under /pack can be made");
    let at = dir;
    const chain = [];
    while (at.startsWith("/pack") && at !== "/pack" && !fs.existsSync(at)) { chain.unshift(at); at = path.dirname(at); }
    fs.mkdirSync(dir, { recursive: true });
    for (const made_ of chain) fs.chownSync(made_, owner, owner);
    made.push(dir);
  }
  for (const copy of body.copies ?? []) {
    if (!copy.to.startsWith("/pack/") || copy.to.includes("..")) throw new Error("only files under /pack can be made");
    const source = copy.from === "big" ? await bigVideo() : (await makeBaseVideos())[0];
    fs.copyFileSync(source, copy.to);
    fs.chownSync(copy.to, owner, owner);
  }
  return { dirs: made, files: (body.copies ?? []).length };
}

// A single-file release: the film alone, named like the release (the torrent has one file and no folder).
// variant "short": the web seed stops before the last piece, so a client ends just short of 100%.
// variant "dummy": a torrent of 40 MB nobody serves (no tracker, no web seed): no data and no peers, ever.
async function newSingleFileFilm(title, year, variant = "complete") {
  const release = `${title}.${year}.1080p.WEB-DL.x264-E2E`;
  const file = path.join(WORK, "films", `${release}.mkv`);
  if (fs.existsSync(file)) throw new Error("a release of that name already exists");
  fs.mkdirSync(path.join(WORK, "films"), { recursive: true });
  if (variant === "dummy") {
    const bytes = randomBytes(40 * 1024 * 1024);
    const hashes = [];
    for (let at = 0; at < bytes.length; at += PIECE) hashes.push(sha1(bytes.subarray(at, at + PIECE)));
    const dummyInfo = { name: `${release}.mkv`, "piece length": PIECE, pieces: Buffer.concat(hashes), length: bytes.length };
    const dummyHash = sha1(bencode(dummyInfo)).toString("hex");
    fs.mkdirSync(path.join(WORK, "torrents"), { recursive: true });
    const dummyName = `${release}.torrent`;
    fs.writeFileSync(path.join(WORK, "torrents", dummyName), bencode({ info: dummyInfo, comment: "Deluno end-to-end fixture (nobody has it)" }));
    RELEASES.push({ title: release, torrent: dummyName, infohash: dummyHash, size: bytes.length, cat: 2000, seeders: 50 });
    log(`made ${release}: ${bytes.length} bytes, one file, no web seed and no tracker, hash ${dummyHash}`);
    return { title, year, release, infohash: dummyHash, size: bytes.length, url: `${PUBLIC_BASE}/dl/${dummyName}`, files: [{ path: `${release}.mkv`, size: bytes.length }] };
  }
  const [baseFilm] = await makeBaseVideos();
  await ffmpeg(["-i", baseFilm, "-c", "copy", "-metadata", `title=${release}`, file]);
  if (variant === "short") SHORT_FILES.add(`${release}.mkv`);
  const data = fs.readFileSync(file);
  const pieces = [];
  for (let at = 0; at < data.length; at += PIECE) pieces.push(sha1(data.subarray(at, at + PIECE)));
  const info = { name: `${release}.mkv`, "piece length": PIECE, pieces: Buffer.concat(pieces), length: data.length };
  const infohash = sha1(bencode(info)).toString("hex");
  // The web seed of a one-file torrent is fetched as <base><name>, so the file sits directly under /webseed/.
  const torrent = bencode({ info, "url-list": [`${PUBLIC_BASE}/webseed/`], comment: "Deluno end-to-end fixture" });
  fs.mkdirSync(path.join(WORK, "torrents"), { recursive: true });
  const name = `${release}.torrent`;
  fs.writeFileSync(path.join(WORK, "torrents", name), torrent);
  RELEASES.push({ title: release, torrent: name, infohash, size: data.length, cat: 2000, seeders: 50 });
  log(`made ${release}: ${data.length} bytes, one file, hash ${infohash}`);
  return { title, year, release, infohash, size: data.length, url: `${PUBLIC_BASE}/dl/${name}`, files: [{ path: `${release}.mkv`, size: data.length }] };
}

// A season pack: a folder named like a scene release with one episode file for each of 1..episodes (each its own bytes), one torrent over the
// lot, offered on the Torznab feed under the TV categories (a tvsearch finds it, a movie search does not).
async function newTvPack(title, year, episodes) {
  const release = `${title}.S01.1080p.WEB-DL.x264-E2E`;
  const folder = path.join(WORK, "films", release);
  if (fs.existsSync(folder)) throw new Error("a release of that name already exists");
  const [baseFilm] = await makeBaseVideos();
  fs.mkdirSync(folder, { recursive: true });
  for (let episode = 1; episode <= episodes; episode++) {
    const name = `${title}.S01E${String(episode).padStart(2, "0")}.1080p.WEB-DL.x264-E2E.mkv`;
    await ffmpeg(["-i", baseFilm, "-c", "copy", "-metadata", `title=${name}`, path.join(folder, name)]);
  }
  const { torrent, infohash, total, files } = makeTorrent(folder, release, `${PUBLIC_BASE}/webseed/`);
  fs.mkdirSync(path.join(WORK, "torrents"), { recursive: true });
  const name = `${release}.torrent`;
  fs.writeFileSync(path.join(WORK, "torrents", name), torrent);
  RELEASES.push({ title: release, torrent: name, infohash, size: total, cat: 5040, seeders: 50, tv: true });
  log(`made ${release}: ${total} bytes, ${files.length} episode files, hash ${infohash}`);
  return { title, year, release, infohash, size: total, url: `${PUBLIC_BASE}/dl/${name}`, files: files.map((f) => ({ path: f, size: fs.statSync(path.join(folder, f)).size })) };
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

// ------------------------------------------------------------------ Usenet: yEnc articles, the .nzb and the NNTP server

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[i] = c >>> 0;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return ((c ^ 0xffffffff) >>> 0).toString(16).padStart(8, "0");
}

// One article's body: =ybegin, =ypart, the data yEnc-encoded in 128-character lines, =yend (CRLF line ends).
function yencPart(data, begin, part, total, size, name, wholeCrc, last) {
  const head = Buffer.from(`=ybegin part=${part} total=${total} line=128 size=${size} name=${name}\r\n=ypart begin=${begin} end=${begin + data.length - 1}\r\n`);
  const body = Buffer.alloc(data.length * 2 + Math.ceil(data.length / 64) + 16);
  let at = 0;
  let col = 0;
  for (let i = 0; i < data.length; i++) {
    const o = (data[i] + 42) & 0xff;
    const critical = o === 0 || o === 10 || o === 13 || o === 61 || (col === 0 && (o === 46 || o === 9 || o === 32));
    if (critical) {
      if (col >= 127) { body[at++] = 13; body[at++] = 10; col = 0; }
      body[at++] = 61; body[at++] = (o + 64) & 0xff; col += 2;
    } else { body[at++] = o; col++; }
    if (col >= 128) { body[at++] = 13; body[at++] = 10; col = 0; }
  }
  if (col > 0) { body[at++] = 13; body[at++] = 10; }
  const tail = Buffer.from(`=yend size=${data.length} part=${part} pcrc32=${crc32(data)}${last ? ` crc32=${wholeCrc}` : ""}\r\n`);
  return Buffer.concat([head, body.subarray(0, at), tail]);
}

// Posts a file as articles; returns what the .nzb lists for it.
function postFile(file, postedName, index, count, releaseKey) {
  const data = fs.readFileSync(file);
  const total = Math.max(1, Math.ceil(data.length / PART_BYTES));
  const whole = crc32(data);
  const segments = [];
  for (let part = 1; part <= total; part++) {
    const slice = data.subarray((part - 1) * PART_BYTES, part * PART_BYTES);
    const body = yencPart(slice, (part - 1) * PART_BYTES + 1, part, total, data.length, postedName, whole, part === total);
    const id = `e2e.${releaseKey}.${index}.${part}@e2e.test`;
    const subject = `[${index}/${count}] - "${postedName}" yEnc (${part}/${total}) ${data.length}`;
    const head = `Message-ID: <${id}>\r\nNewsgroups: ${GROUP}\r\nSubject: ${subject}\r\nFrom: e2e <e2e@e2e.test>\r\nDate: Mon, 01 Jan 2024 00:00:00 +0000\r\n`;
    ARTICLES.set(id, { head: Buffer.from(head), body });
    segments.push({ number: part, bytes: body.length, id });
  }
  return { name: postedName, size: data.length, total, segments };
}

const esc = (text) => text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

function nzbXml(name, files) {
  const lines = ['<?xml version="1.0" encoding="UTF-8"?>',
    '<!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">',
    '<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">', `  <head><meta type="name">${esc(name)}</meta></head>`];
  files.forEach((f, i) => {
    const subject = `[${i + 1}/${files.length}] - "${f.name}" yEnc (1/${f.total}) ${f.size}`;
    lines.push(`  <file poster="e2e &lt;e2e@e2e.test&gt;" date="1704067200" subject="${esc(subject)}">`,
      `    <groups><group>${GROUP}</group></groups>`, "    <segments>");
    for (const s of f.segments) lines.push(`      <segment bytes="${s.bytes}" number="${s.number}">${s.id}</segment>`);
    lines.push("    </segments>", "  </file>");
  });
  lines.push("</nzb>");
  return lines.join("\n");
}

// A Usenet release: a folder job (the film and a small .nfo beside it, as a real post carries them) and its .nzb.
// hang "all": no article is ever answered; hang "last": the film's last article is never answered; slow: every article waits SLOW_MS.
async function newUsenetFilm(title, year, { hang = "", slow = false } = {}) {
  const release = `${title}.${year}.1080p.WEB-DL.x264-E2E`;
  if (RELEASES.some((r) => r.title === release)) throw new Error("a release of that name already exists");
  const [baseFilm] = await makeBaseVideos();
  const folder = path.join(WORK, "nzb-films", release);
  fs.mkdirSync(folder, { recursive: true });
  const film = path.join(folder, `${release}.mkv`);
  await ffmpeg(["-i", baseFilm, "-c", "copy", "-metadata", `title=${release}`, film]);
  const nfo = path.join(folder, `${release}.nfo`);
  // Plain sentences: a line ending in eight hex digits reads as an SFV checksum line to SABnzbd.
  fs.writeFileSync(nfo, Array.from({ length: 40 }, (_, i) => `Line ${i + 1} of the note that travels beside the film in this release.`).join("\n") + "\n");
  const key = sha1(Buffer.from(release)).toString("hex").slice(0, 10);
  const files = [postFile(film, `${release}.mkv`, 1, 2, key), postFile(nfo, `${release}.nfo`, 2, 2, key)];
  if (hang === "all") for (const f of files) for (const s of f.segments) HUNG.add(s.id);
  if (hang === "last") HUNG.add(files[0].segments[files[0].segments.length - 1].id);
  if (slow) for (const f of files) for (const s of f.segments) SLOWED.add(s.id);
  fs.mkdirSync(path.join(WORK, "nzbs"), { recursive: true });
  const name = `${release}.nzb`;
  fs.writeFileSync(path.join(WORK, "nzbs", name), nzbXml(release, files));
  const size = files.reduce((sum, f) => sum + f.size, 0);
  RELEASES.push({ title: release, torrent: name, infohash: key, size, cat: 2000, seeders: 0, usenet: true });
  log(`made ${release}: ${size} bytes, ${files.length} files, ${files.reduce((n, f) => n + f.total, 0)} articles`);
  return { title, year, release, protocol: "usenet", infohash: key, size, url: `${PUBLIC_BASE}/dl/${name}`, files: files.map((f) => ({ path: f.name, size: f.size })) };
}

// A news server for those articles. It speaks what a downloader needs and accepts any login.
const nntp = net.createServer((socket) => {
  socket.setNoDelay(true);
  log(`news: ${socket.remoteAddress} connected`);
  socket.on("error", () => {});
  socket.write("200 E2E news server ready (no posting)\r\n");
  let pending = "";
  socket.on("data", (chunk) => {
    pending += chunk.toString("latin1");
    let at;
    while ((at = pending.indexOf("\n")) >= 0) {
      const line = pending.slice(0, at).replace(/\r$/, "");
      pending = pending.slice(at + 1);
      nntpCommand(socket, line.trim());
    }
  });
});

// Lines that start with "." are doubled; the text always ends with CRLF.
function dotStuffed(buf) {
  const parts = [];
  let start = 0;
  for (let i = 0; i < buf.length; i++) {
    if (buf[i] === 10) {
      if (buf[start] === 46) parts.push(Buffer.from("."));
      parts.push(buf.subarray(start, i + 1));
      start = i + 1;
    }
  }
  if (start < buf.length) {
    if (buf[start] === 46) parts.push(Buffer.from("."));
    parts.push(buf.subarray(start), Buffer.from("\r\n"));
  }
  return Buffer.concat(parts);
}

function nntpCommand(socket, line) {
  if (!line) return;
  log(`news: ${line.replace(/(AUTHINFO PASS).*/i, "$1 ...")}`);
  const [verb, ...rest] = line.split(" ");
  const arg = rest.join(" ");
  const command = verb.toUpperCase();
  switch (command) {
    case "QUIT": socket.end("205 Goodbye\r\n"); return;
    case "CAPABILITIES": socket.write("101 Capability list:\r\nVERSION 2\r\nREADER\r\nAUTHINFO USER\r\nIMPLEMENTATION E2E\r\n.\r\n"); return;
    case "MODE": socket.write("200 Reader mode, posting prohibited\r\n"); return;
    case "AUTHINFO": socket.write(/^USER/i.test(arg) ? "381 Password required\r\n" : "281 Authentication accepted\r\n"); return;
    case "GROUP": socket.write(`211 1000 1 1000 ${arg || GROUP}\r\n`); return;
    case "DATE": socket.write(`111 ${new Date().toISOString().replace(/[-:T]/g, "").slice(0, 14)}\r\n`); return;
    case "STAT": case "ARTICLE": case "HEAD": case "BODY": {
      const id = arg.replace(/^</, "").replace(/>$/, "");
      const article = ARTICLES.get(id);
      if (!article) { socket.write("430 No such article\r\n"); return; }
      // A hung article is never answered: the connection waits, as it does for a news server that has stopped replying.
      if (HUNG.has(id)) { log(`news: holding ${command} <${id}> unanswered`); return; }
      if (command === "STAT") { socket.write(`223 0 <${id}>\r\n`); return; }
      const parts = [Buffer.from(`${{ ARTICLE: "220", HEAD: "221", BODY: "222" }[command]} 0 <${id}>\r\n`)];
      if (command !== "BODY") parts.push(dotStuffed(article.head));
      if (command === "ARTICLE") parts.push(Buffer.from("\r\n"));
      if (command !== "HEAD") parts.push(dotStuffed(article.body));
      parts.push(Buffer.from(".\r\n"));
      const out = Buffer.concat(parts);
      const send = () => socket.write(out, () => log(`news: sent ${command} <${id}> (${out.length} bytes)`));
      if (SLOWED.has(id) && command !== "STAT") setTimeout(send, SLOW_MS); else send();
      return;
    }
    default: socket.write("500 Command not recognised\r\n");
  }
}

// ------------------------------------------------------------------ the feed

const CAPS = '<?xml version="1.0" encoding="UTF-8"?><caps><server version="1.0" title="E2E feed" />' +
  '<limits max="100" default="100"/><searching><search available="yes" supportedParams="q"/>' +
  '<tv-search available="yes" supportedParams="q,season,ep,tvdbid"/>' +
  '<movie-search available="yes" supportedParams="q,imdbid,tmdbid"/></searching>' +
  '<categories><category id="2000" name="Movies"><subcat id="2040" name="Movies/HD"/><subcat id="2045" name="Movies/UHD"/></category>' +
  '<category id="5000" name="TV"><subcat id="5040" name="TV/HD"/><subcat id="5045" name="TV/UHD"/></category></categories></caps>';


function searchXml(query, usenet) {
  const words = (query.get("q") || "").toLowerCase().split(/[ .\-_()]/).filter((w) => w.length >= 3);
  const kind = (query.get("t") || "").toLowerCase();
  const items = [];
  for (const row of [...RELEASES]) {
    if (Boolean(row.tv) !== (kind === "tvsearch" || kind === "tv")) continue;
    if (Boolean(row.usenet) !== usenet) continue;
    if (words.length && !words.some((w) => row.title.toLowerCase().includes(w))) continue;
    const url = `${PUBLIC_BASE}/dl/${row.torrent}`;
    if (usenet) {
      items.push(
        `<item><title>${esc(row.title)}</title><guid isPermaLink="false">${row.infohash}</guid><link>${url}</link>` +
        `<pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><size>${row.size}</size>` +
        `<enclosure url="${url}" length="${row.size}" type="application/x-nzb"/>` +
        `<newznab:attr name="category" value="${row.cat}"/><newznab:attr name="size" value="${row.size}"/></item>`);
      continue;
    }
    items.push(
      `<item><title>${esc(row.title)}</title><guid isPermaLink="false">${row.infohash}</guid><link>${url}</link>` +
      `<pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><size>${row.size}</size>` +
      `<enclosure url="${url}" length="${row.size}" type="application/x-bittorrent"/>` +
      `<torznab:attr name="category" value="${row.cat}"/><torznab:attr name="seeders" value="${row.seeders}"/>` +
      `<torznab:attr name="peers" value="${row.seeders + 2}"/><torznab:attr name="infohash" value="${row.infohash}"/>` +
      `<torznab:attr name="size" value="${row.size}"/></item>`);
  }
  return '<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" ' +
    'xmlns:torznab="http://torznab.com/schemas/2015/feed" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/"><channel><title>E2E feed</title><description>local</description>' +
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

// A web-seed file that stops short: what lies before the last piece is served, and a request that reaches the last piece is
// answered up to that point and then left open, so a client ends with every piece but the last.
function sendFileShort(req, res, file) {
  const size = fs.statSync(file).size;
  const limit = Math.floor((size - 1) / PIECE) * PIECE; // the first byte of the last piece
  let start = 0;
  let end = size - 1;
  const match = /^bytes=(\d*)-(\d*)$/.exec((req.headers.range || "").trim());
  if (match) {
    if (match[1] === "" && match[2]) start = Math.max(0, size - Number(match[2]));
    else { start = Number(match[1] || 0); if (match[2]) end = Math.min(size - 1, Number(match[2])); }
    if (start > end || start >= size) return sendBytes(req, res, 416, "", "text/plain", { "Content-Range": `bytes */${size}` });
  }
  const headers = { "Content-Type": "application/octet-stream", "Content-Length": end - start + 1, "Accept-Ranges": "bytes" };
  if (match) headers["Content-Range"] = `bytes ${start}-${end}/${size}`;
  res.writeHead(match ? 206 : 200, headers);
  if (req.method === "HEAD") return res.end();
  const stopAt = Math.min(end, limit - 1);
  if (start > stopAt) { log(`web seed: holding the last piece of ${path.basename(file)} unanswered`); return; }
  const stream = fs.createReadStream(file, { start, end: stopAt });
  stream.on("error", () => res.destroy());
  res.on("close", () => stream.destroy());
  stream.pipe(res, { end: stopAt === end }); // the rest of the range is never sent
}

async function handleGet(req, res, url) {
  const route = decodeURIComponent(url.pathname);
  const query = url.searchParams;
  if (route === "/health") return sendBytes(req, res, 200, "ok", "text/plain");
  // /private/api is the same Torznab feed under another address, so Deluno can be given it as a private tracker (group keepseeding).
  if (route === "/api" || route === "/newznab/api" || route === "/private/api") {
    const kind = (query.get("t") || "").toLowerCase();
    return sendBytes(req, res, 200, kind === "caps" ? CAPS : searchXml(query, route === "/newznab/api"));
  }
  if (route.startsWith("/dl/")) {
    const target = path.join(WORK, "torrents", path.basename(route));
    if (fs.existsSync(target)) return sendBytes(req, res, 200, fs.readFileSync(target), "application/x-bittorrent");
    const nzb = path.join(WORK, "nzbs", path.basename(route));
    if (route.endsWith(".nzb") && fs.existsSync(nzb)) return sendBytes(req, res, 200, fs.readFileSync(nzb), "application/x-nzb");
    return sendBytes(req, res, 404, "no", "text/plain");
  }
  if (route.startsWith("/webseed/")) {
    const rel = path.posix.normalize(route.slice("/webseed/".length));
    const target = path.join(WORK, "films", rel);
    if (rel.startsWith("..") || !fs.existsSync(target) || !fs.statSync(target).isFile()) return sendBytes(req, res, 404, "no", "text/plain");
    if (SHORT_FILES.has(rel)) return sendFileShort(req, res, target);
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
  // A metadata broker (docs/metadata-broker-contract.md) that knows the made-up shows the season-pack group adds: a show whose title begins
  // "Gppack" has one season of five episodes. It answers nothing else, so no other title gets metadata from it.
  if (route === "/metadata/search") {
    const title = query.get("query") || "";
    const results = title.startsWith("Gppack") ? [{
      provider: "tmdb", providerId: String(9000000 + (Number.parseInt(sha1(Buffer.from(title)).toString("hex").slice(0, 5), 16) % 900000)), mediaType: "tv", title,
      originalTitle: title, year: 2020, overview: "A made-up show for the end-to-end suite.", posterUrl: null, backdropUrl: null, rating: null, ratings: [], genres: [], imdbId: null, externalUrl: null
    }] : [];
    return sendBytes(req, res, 200, JSON.stringify({ provider: "deluno-broker", mode: "broker", resultCount: results.length, results }), "application/json");
  }
  const catalogue = /^\/metadata\/tv\/(\d+)\/catalogue$/.exec(route);
  if (catalogue) {
    const episodes = [1, 2, 3, 4, 5].map((episodeNumber) => ({ episodeNumber, title: `Episode ${episodeNumber}`, overview: "", airDate: `2020-01-0${episodeNumber}` }));
    return sendBytes(req, res, 200, JSON.stringify({ seasons: [{ seasonNumber: 1, name: "Season 1", airDate: "2020-01-01", episodes }] }), "application/json");
  }
  if (route === "/sha256") {
    const root = ROOTS[query.get("root") || ""];
    const target = path.posix.normalize(query.get("path") || "");
    if (!root || !target.startsWith(root + "/") || !fs.existsSync(target) || !fs.statSync(target).isFile()) return sendBytes(req, res, 404, "no", "text/plain");
    const hash = createHash("sha256");
    for await (const chunk of fs.createReadStream(target)) hash.update(chunk);
    return sendBytes(req, res, 200, JSON.stringify({ sha256: hash.digest("hex"), size: fs.statSync(target).size }), "application/json");
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
    if (url.pathname === "/stage") return sendBytes(req, res, 200, JSON.stringify(await stage(body)), "application/json");
    if (url.pathname === "/films") {
      if (body.tvPack) return sendBytes(req, res, 200, JSON.stringify(await newTvPack(body.title, Number.parseInt(body.year ?? 2020, 10), Number(body.tvPack))), "application/json");
      if (body.usenet) return sendBytes(req, res, 200, JSON.stringify(await newUsenetFilm(body.title, Number.parseInt(body.year ?? 2020, 10), { hang: body.hang || "", slow: Boolean(body.slow) })), "application/json");
      if (body.layout === "file" || body.variant) return sendBytes(req, res, 200, JSON.stringify(await newSingleFileFilm(body.title, Number.parseInt(body.year ?? 2020, 10), body.variant || "complete")), "application/json");
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
nntp.listen(NNTP_PORT, "0.0.0.0");
log(`news server on ${NNTP_PORT}`);
