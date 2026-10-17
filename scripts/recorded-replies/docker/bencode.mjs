// Just enough bencode to put a tracker into a torrent the fixture made (its info dictionary, and so its info hash, is untouched).
export function decode(buffer, at = { i: 0 }) {
  const c = String.fromCharCode(buffer[at.i]);
  if (c === 'i') { const e = buffer.indexOf(0x65, at.i); const n = Number(buffer.subarray(at.i + 1, e).toString('latin1')); at.i = e + 1; return n; }
  if (c === 'l') { at.i++; const out = []; while (buffer[at.i] !== 0x65) out.push(decode(buffer, at)); at.i++; return out; }
  if (c === 'd') { at.i++; const out = new Map(); while (buffer[at.i] !== 0x65) { const k = decode(buffer, at); out.set(k.toString('latin1'), decode(buffer, at)); } at.i++; return out; }
  const colon = buffer.indexOf(0x3a, at.i); const len = Number(buffer.subarray(at.i, colon).toString('latin1'));
  const out = buffer.subarray(colon + 1, colon + 1 + len); at.i = colon + 1 + len; return Buffer.from(out);
}
export function encode(value) {
  if (Buffer.isBuffer(value)) return Buffer.concat([Buffer.from(`${value.length}:`), value]);
  if (typeof value === 'string') return encode(Buffer.from(value));
  if (typeof value === 'number') return Buffer.from(`i${value}e`);
  if (Array.isArray(value)) return Buffer.concat([Buffer.from('l'), ...value.map(encode), Buffer.from('e')]);
  if (value instanceof Map) {
    const keys = [...value.keys()].sort((a, b) => Buffer.compare(Buffer.from(a, 'latin1'), Buffer.from(b, 'latin1')));
    return Buffer.concat([Buffer.from('d'), ...keys.flatMap((k) => [encode(Buffer.from(k, 'latin1')), encode(value.get(k))]), Buffer.from('e')]);
  }
  throw new Error('cannot bencode ' + typeof value);
}
/** The same torrent with an announce address added. */
export function withAnnounce(bytes, announce) {
  const root = decode(Buffer.from(bytes));
  root.set('announce', Buffer.from(announce));
  return new Uint8Array(encode(root));
}
