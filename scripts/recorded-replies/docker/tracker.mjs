// A small HTTP tracker and torrent-file server for the rTorrent recorder (rtorrent.mjs), run in a node container beside the two rTorrent
// containers. It is the only part of the stack that is not a real app, and nothing it says is recorded: it only introduces the two
// clients to each other (compact peer lists) so one really downloads from the other, and serves the .torrent files by link.
//   POST /torrents/<name>   body = the torrent file          GET /dl/<name>.torrent    the file again
//   POST /fail/<infohash hex>   the tracker then answers that torrent with a failure reason (a tracker that is up and refuses)
import http from 'node:http';

const torrents = new Map();
const peers = new Map(); // infohash(latin1) -> Map(ip:port -> {ip,port,at})
const failing = new Set();

const bencodeString = (b) => Buffer.concat([Buffer.from(`${b.length}:`), b]);
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://tracker');
  const chunks = []; for await (const c of req) chunks.push(c);
  const body = Buffer.concat(chunks);
  if (req.method === 'POST' && url.pathname.startsWith('/torrents/')) { torrents.set(decodeURIComponent(url.pathname.slice(10)), body); res.end('ok'); return; }
  if (req.method === 'POST' && url.pathname.startsWith('/fail/')) { failing.add(url.pathname.slice(6).toLowerCase()); res.end('ok'); return; }
  if (url.pathname.startsWith('/dl/')) { const t = torrents.get(decodeURIComponent(url.pathname.slice(4))); if (!t) { res.statusCode = 404; res.end('not found'); return; } res.setHeader('content-type', 'application/x-bittorrent'); res.end(t); return; }
  if (url.pathname === '/announce') {
    const found = /info_hash=([^&]+)/.exec(req.url);
    if (!found) { res.statusCode = 400; res.end(); return; }
    const raw = found[1];
    const hash = Buffer.from(raw.replace(/%([0-9a-fA-F]{2})|(.)/g, (m, h, c) => h ? String.fromCharCode(parseInt(h, 16)) : c), 'latin1');
    const hex = hash.toString('hex');
    if (failing.has(hex)) { res.end(Buffer.concat([Buffer.from('d14:failure reason'), bencodeString(Buffer.from('Registered torrents only')), Buffer.from('e')])); return; }
    const ip = (req.socket.remoteAddress ?? '').replace('::ffff:', '');
    const port = Number(url.searchParams.get('port'));
    const set = peers.get(hex) ?? new Map(); peers.set(hex, set);
    set.set(`${ip}:${port}`, { ip, port });
    console.log('announce', hex, ip, port, url.searchParams.get('event'), 'others', set.size - 1);
    const compact = Buffer.concat([...set.values()].filter((p) => `${p.ip}:${p.port}` !== `${ip}:${port}`).map((p) => Buffer.from([...p.ip.split('.').map(Number), p.port >> 8, p.port & 255])));
    res.end(Buffer.concat([Buffer.from('d8:completei1e10:incompletei1e8:intervali15e12:min intervali5e5:peers'), bencodeString(compact), Buffer.from('e')]));
    return;
  }
  res.statusCode = 404; res.end();
}).listen(8080);
