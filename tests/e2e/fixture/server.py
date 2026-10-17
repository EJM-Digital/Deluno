"""The end-to-end suite's fixture (tests/e2e, docs/exec-plans/active/PLAN-E2E-IN-CI.md).

What it does, with only the Python standard library and ffmpeg:

  * makes, once at start, a small film that is a real video (ffmpeg's test pattern and a tone, encoded as Matroska) and a
    tiny extra (a featurette);
  * POST /films makes a release of its own from them (a folder named like a scene release: the film, a sample, an .nfo and,
    when asked, a featurette), hashes it into a real .torrent and offers it on the Torznab feed;
  * the .torrent carries no tracker. It names this server as a web seed (BEP 19, "url-list"), so a download client
    given the torrent really downloads every byte of it from /webseed/ over HTTP, with no internet and no second client;
  * GET /api?t=caps|search|movie|tvsearch is the Torznab feed;  GET /dl/<name>.torrent is the torrent;
  * GET /inspect?root=media|downloads|weir&contains=<text> lists the files under a shared folder (read-only), and
    GET /probe?root=...&path=... is ffprobe's reading of one, so a spec can say what is on disk, and that it is a video,
    without trusting any application's word for it.

Nothing here is the product: Deluno, the download client and Weir are the real published images.
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse

PORT = int(os.environ.get("FIXTURE_PORT", "8765"))
# The name other containers reach this one by; the web seed address inside every torrent.
PUBLIC_BASE = os.environ.get("FIXTURE_PUBLIC_BASE", f"http://fixture:{PORT}")
WORK = os.environ.get("FIXTURE_WORK", "/work")
FILM_MB = float(os.environ.get("FIXTURE_FILM_MB", "24"))
ROOTS = {"media": "/media", "downloads": "/downloads", "weir": "/weir"}
PIECE = 256 * 1024

LOCK = threading.Lock()
RELEASES = []  # newest last: title, torrent, infohash, size, cat, seeders


def log(msg):
    print(time.strftime("%H:%M:%S"), msg, flush=True)


# ------------------------------------------------------------------ bencode and the torrent

def bstr(value):
    data = value if isinstance(value, bytes) else value.encode("utf-8")
    return str(len(data)).encode() + b":" + data


def bencode(value):
    if isinstance(value, bool):
        raise TypeError("no booleans in a torrent")
    if isinstance(value, int):
        return b"i" + str(value).encode() + b"e"
    if isinstance(value, (bytes, str)):
        return bstr(value)
    if isinstance(value, list):
        return b"l" + b"".join(bencode(v) for v in value) + b"e"
    if isinstance(value, dict):
        out = b"d"
        for key in sorted(value, key=lambda k: k if isinstance(k, bytes) else k.encode()):
            out += bstr(key) + bencode(value[key])
        return out + b"e"
    raise TypeError(type(value))


def make_torrent(folder, name, web_seed_base):
    """A multi-file torrent over every file under folder (path order), with a web seed and no tracker."""
    files = []
    for root, _dirs, names in os.walk(folder):
        for n in names:
            full = os.path.join(root, n)
            files.append(os.path.relpath(full, folder).replace(os.sep, "/"))
    files.sort()
    entries, pieces, buf, total = [], b"", b"", 0
    for rel in files:
        size = os.path.getsize(os.path.join(folder, rel))
        entries.append({"length": size, "path": rel.split("/")})
        total += size
        with open(os.path.join(folder, rel), "rb") as handle:
            while True:
                chunk = handle.read(PIECE - len(buf))
                if not chunk:
                    break
                buf += chunk
                if len(buf) == PIECE:
                    pieces += hashlib.sha1(buf).digest()
                    buf = b""
    if buf:
        pieces += hashlib.sha1(buf).digest()
    info = {"name": name, "piece length": PIECE, "pieces": pieces, "files": entries}
    infohash = hashlib.sha1(bencode(info)).hexdigest()
    torrent = bencode({"info": info, "url-list": [web_seed_base], "comment": "Deluno end-to-end fixture"})
    return torrent, infohash, total, files


# ------------------------------------------------------------------ the films

def ffmpeg(args):
    result = subprocess.run(["ffmpeg", "-y", "-v", "error", *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError("ffmpeg failed: " + result.stderr[-400:])


def make_base_videos():
    os.makedirs(os.path.join(WORK, "base"), exist_ok=True)
    film = os.path.join(WORK, "base", "film.mkv")
    extra = os.path.join(WORK, "base", "extra.mkv")
    if not os.path.exists(film):
        # A real, playable film: 640x360 test pattern with a tone. The bitrate is set so the file is about FILM_MB large.
        seconds = 130  # Deluno treats a film under two minutes as a likely sample
        rate_kbps = int(FILM_MB * 8 * 1024 / seconds)
        ffmpeg(["-f", "lavfi", "-i", f"testsrc2=size=640x360:rate=24:duration={seconds}",
                "-f", "lavfi", "-i", f"sine=frequency=440:sample_rate=48000:duration={seconds}",
                "-c:v", "mpeg4", "-b:v", f"{rate_kbps}k", "-minrate", f"{rate_kbps}k", "-maxrate", f"{rate_kbps}k",
                "-bufsize", f"{rate_kbps}k", "-c:a", "aac", "-b:a", "96k", "-shortest", film])
    if not os.path.exists(extra):
        ffmpeg(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:duration=4",
                "-c:v", "mpeg4", "-b:v", "200k", "-c:a", "aac", "-b:a", "48k", "-shortest", extra])
    return film, extra


def new_film(title, year, with_extra):
    release = f"{title}.{year}.1080p.WEB-DL.x264-E2E"
    folder = os.path.join(WORK, "films", release)
    if os.path.isdir(folder):
        raise ValueError("a release of that name already exists")
    base_film, base_extra = make_base_videos()
    os.makedirs(os.path.join(folder, "Sample"), exist_ok=True)
        # Each release is its own bytes (a stream copy with the release's name as the file's title), so no two share a piece.
    ffmpeg(["-i", base_film, "-c", "copy", "-metadata", f"title={release}", os.path.join(folder, f"{release}.mkv")])
    with open(base_extra, "rb") as src, open(os.path.join(folder, "Sample", "sample.mkv"), "wb") as dst:
        dst.write(src.read())
    with open(os.path.join(folder, f"{release}.nfo"), "w") as handle:
        handle.write("end-to-end fixture release\n")
    if with_extra:
        os.makedirs(os.path.join(folder, "Featurettes"), exist_ok=True)
        with open(base_extra, "rb") as src, open(os.path.join(folder, "Featurettes", "featurette.mkv"), "wb") as dst:
            dst.write(src.read())
    torrent, infohash, total, files = make_torrent(folder, release, f"{PUBLIC_BASE}/webseed/")
    os.makedirs(os.path.join(WORK, "torrents"), exist_ok=True)
    name = f"{release}.torrent"
    with open(os.path.join(WORK, "torrents", name), "wb") as handle:
        handle.write(torrent)
    row = {"title": release, "torrent": name, "infohash": infohash, "size": total, "cat": 2000, "seeders": 50}
    with LOCK:
        RELEASES.append(row)
    log(f"made {release}: {total} bytes, {len(files)} files, hash {infohash}")
    return {"title": title, "year": year, "release": release, "infohash": infohash, "size": total,
            "url": f"{PUBLIC_BASE}/dl/{name}",
            "files": [{"path": f, "size": os.path.getsize(os.path.join(folder, f))} for f in files]}


# ------------------------------------------------------------------ the feed

CAPS = ('<?xml version="1.0" encoding="UTF-8"?><caps><server version="1.0" title="E2E feed" />'
        '<limits max="100" default="100"/><searching><search available="yes" supportedParams="q"/>'
        '<tv-search available="yes" supportedParams="q,season,ep,tvdbid"/>'
        '<movie-search available="yes" supportedParams="q,imdbid,tmdbid"/></searching>'
        '<categories><category id="2000" name="Movies"><subcat id="2040" name="Movies/HD"/><subcat id="2045" name="Movies/UHD"/></category>'
        '<category id="5000" name="TV"><subcat id="5040" name="TV/HD"/><subcat id="5045" name="TV/UHD"/></category></categories></caps>')


def esc(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def search_xml(query):
    words = [w for w in re.split(r"[ .\-_()]", query.get("q", [""])[0].lower()) if len(w) >= 3]
    kind = query.get("t", [""])[0].lower()
    items = []
    with LOCK:
        rows = list(RELEASES)
    for row in rows:
        if kind in ("tvsearch", "tv"):
            continue
        if words and not any(w in row["title"].lower() for w in words):
            continue
        url = f"{PUBLIC_BASE}/dl/{row['torrent']}"
        items.append(
            f"<item><title>{esc(row['title'])}</title><guid isPermaLink=\"false\">{row['infohash']}</guid><link>{url}</link>"
            f"<pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><size>{row['size']}</size>"
            f"<enclosure url=\"{url}\" length=\"{row['size']}\" type=\"application/x-bittorrent\"/>"
            f"<torznab:attr name=\"category\" value=\"{row['cat']}\"/><torznab:attr name=\"seeders\" value=\"{row['seeders']}\"/>"
            f"<torznab:attr name=\"peers\" value=\"{row['seeders'] + 2}\"/><torznab:attr name=\"infohash\" value=\"{row['infohash']}\"/>"
            f"<torznab:attr name=\"size\" value=\"{row['size']}\"/></item>")
    return ('<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" '
            'xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><title>E2E feed</title><description>local</description>'
            f'<link>{PUBLIC_BASE}/</link><torznab:response offset="0" total="{len(items)}"/>' + "".join(items) + "</channel></rss>")


# ------------------------------------------------------------------ http

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        log("%s %s" % (self.address_string(), fmt % args))

    def send_bytes(self, status, body, ctype="application/xml", extra=None):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Accept-Ranges", "bytes")
        for key, value in (extra or {}).items():
            self.send_header(key, value)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def send_file(self, path):
        size = os.path.getsize(path)
        start, end, status = 0, size - 1, 200
        header = self.headers.get("Range")
        if header:
            match = re.match(r"bytes=(\d*)-(\d*)$", header.strip())
            if match:
                if match.group(1) == "" and match.group(2):
                    start = max(0, size - int(match.group(2)))
                else:
                    start = int(match.group(1) or 0)
                    if match.group(2):
                        end = min(size - 1, int(match.group(2)))
                if start > end or start >= size:
                    self.send_bytes(416, b"", "text/plain", {"Content-Range": f"bytes */{size}"})
                    return
                status = 206
        length = end - start + 1
        self.send_response(status)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        if self.command == "HEAD":
            return
        with open(path, "rb") as handle:
            handle.seek(start)
            left = length
            while left > 0:
                chunk = handle.read(min(65536, left))
                if not chunk:
                    break
                self.wfile.write(chunk)
                left -= len(chunk)

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        parsed = urlparse(self.path)
        path, query = unquote(parsed.path), parse_qs(parsed.query)
        try:
            if path == "/health":
                self.send_bytes(200, b"ok", "text/plain")
            elif path == "/api":
                kind = query.get("t", [""])[0].lower()
                self.send_bytes(200, (CAPS if kind == "caps" else search_xml(query)).encode())
            elif path.startswith("/dl/"):
                target = os.path.join(WORK, "torrents", os.path.basename(path))
                if os.path.exists(target):
                    with open(target, "rb") as handle:
                        self.send_bytes(200, handle.read(), "application/x-bittorrent")
                else:
                    self.send_bytes(404, b"no", "text/plain")
            elif path.startswith("/webseed/"):
                rel = os.path.normpath(path[len("/webseed/"):])
                target = os.path.join(WORK, "films", rel)
                if rel.startswith("..") or not os.path.isfile(target):
                    self.send_bytes(404, b"no", "text/plain")
                else:
                    self.send_file(target)
            elif path == "/inspect":
                root = ROOTS.get(query.get("root", [""])[0])
                needle = query.get("contains", [""])[0].lower()
                found = []
                if root and os.path.isdir(root):
                    for base, _dirs, names in os.walk(root):
                        for n in names:
                            full = os.path.join(base, n)
                            if needle in full.lower():
                                try:
                                    found.append({"path": full, "size": os.path.getsize(full)})
                                except OSError:
                                    pass
                found.sort(key=lambda f: f["path"])
                self.send_bytes(200, json.dumps(found).encode(), "application/json")
            elif path == "/probe":
                root = ROOTS.get(query.get("root", [""])[0])
                target = os.path.normpath(query.get("path", [""])[0])
                if not root or not target.startswith(root + "/") or not os.path.isfile(target):
                    self.send_bytes(404, b"no", "text/plain")
                else:
                    # What a player would find in the file: ffprobe's own reading of it.
                    result = subprocess.run(["ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", target],
                                            capture_output=True, text=True)
                    self.send_bytes(200 if result.returncode == 0 else 422, (result.stdout or json.dumps({"error": result.stderr})).encode(), "application/json")
            else:
                self.send_bytes(404, b"no", "text/plain")
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_POST(self):
        path = urlparse(self.path).path
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length) or b"{}")
        try:
            if path == "/films":
                made = new_film(body["title"], int(body.get("year", 2020)), bool(body.get("extra")))
                self.send_bytes(200, json.dumps(made).encode(), "application/json")
            else:
                self.send_bytes(404, b"no", "text/plain")
        except Exception as error:  # the spec reads the message
            log("error: %s" % error)
            self.send_bytes(500, json.dumps({"error": str(error)}).encode(), "application/json")


def main():
    os.makedirs(WORK, exist_ok=True)
    make_base_videos()
    log(f"base film ready; serving on {PORT}, web seed {PUBLIC_BASE}/webseed/")
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    server.daemon_threads = True
    server.serve_forever()


if __name__ == "__main__":
    sys.exit(main())
