// A recording pass-through between two apps in the e2e stack (#1237, #1239). It runs in a container beside them:
//
//   node proxy.mjs <listen-port>=<target-base-url> ...        e.g.  node proxy.mjs 9348=http://weir:9347 9349=http://deluno:8080
//
// Every request and reply that goes through is kept, in order, and GET /_log on the control port (the last argument is
// `control=<port>`) returns them as JSON; DELETE /_log forgets them. A fault can be queued with POST /_fault:
//   { method, pathPattern, status, body, contentType, forward }   the next matching request is answered with that (and, with
//   forward, still sent on, so the real answer is kept beside it).
// Nothing is changed in what passes through, so what is recorded is what the two apps really said to each other.
import http from 'node:http';

const args = process.argv.slice(2);
const listeners = args.filter((a) => !a.startsWith('control=')).map((a) => { const [port, target] = a.split(/=(.*)/s); return { port: Number(port), target: target.replace(/\/+$/, '') }; });
const control = Number((args.find((a) => a.startsWith('control=')) ?? 'control=9350').split('=')[1]);
const rows = [];
const faults = [];
const skip = new Set(['host', 'content-length', 'connection', 'transfer-encoding', 'keep-alive', 'proxy-connection', 'expect']);

const read = (req) => new Promise((resolve) => { const chunks = []; req.on('data', (c) => chunks.push(c)); req.on('end', () => resolve(Buffer.concat(chunks))); });

for (const { port, target } of listeners) {
  http.createServer(async (req, res) => {
    const body = await read(req);
    const row = { at: new Date().toISOString(), listener: port, method: req.method, path: req.url, requestHeaders: {}, requestBody: body.toString('utf8') };
    for (const name of ['content-type', 'idempotency-key', 'x-webhook-secret', 'accept', 'x-requested-with']) if (req.headers[name]) row.requestHeaders[name] = req.headers[name];
    // The secret header is kept in the log as sent; the recorder removes it (by its key name, and by its value) before anything is written.
    const fault = faults.find((f) => !f.used && (!f.method || f.method === req.method) && new RegExp(f.pathPattern ?? '.*').test(req.url));
    if (fault) fault.used = true;
    let real = null;
    if (!fault || fault.forward) {
      try {
        const forwardHeaders = {};
        for (const [k, v] of Object.entries(req.headers)) if (!skip.has(k)) forwardHeaders[k] = v;
        const r = await fetch(target + req.url, { method: req.method, headers: forwardHeaders, body: ['GET', 'HEAD'].includes(req.method) ? undefined : body });
        real = { status: r.status, contentType: r.headers.get('content-type') ?? '', text: await r.text() };
      } catch (error) { real = { status: 502, contentType: 'text/plain', text: '' , error: String(error.message) }; }
    }
    const answer = fault ? { status: fault.status, contentType: fault.contentType ?? 'application/json', text: fault.body ?? '' } : real;
    row.reply = { status: answer.status, contentType: answer.contentType, bodyText: answer.text };
    if (fault) { row.injected = true; if (real) row.real = { status: real.status, contentType: real.contentType, bodyText: real.text }; }
    rows.push(row);
    res.statusCode = answer.status;
    if (answer.contentType) res.setHeader('content-type', answer.contentType);
    res.end(answer.text);
  }).listen(port, '0.0.0.0');
}

http.createServer(async (req, res) => {
  if (req.url === '/_log' && req.method === 'GET') { res.setHeader('content-type', 'application/json'); return res.end(JSON.stringify(rows)); }
  if (req.url === '/_log' && req.method === 'DELETE') { rows.length = 0; faults.length = 0; return res.end('cleared'); }
  if (req.url === '/_fault' && req.method === 'POST') { faults.push(JSON.parse((await read(req)).toString('utf8'))); return res.end('queued'); }
  res.statusCode = 404; res.end('no');
}).listen(control, '0.0.0.0');
console.log(`proxy: ${listeners.map((l) => `${l.port} -> ${l.target}`).join(', ')}; control ${control}`);
