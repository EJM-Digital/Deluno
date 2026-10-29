// Records what Deluge's own download directory really answers (#1301): the Apache listing of https://ftp.osuosl.org/pub/deluge/windows/ that
// DirectoryListingLatestRelease reads, and the .sha256 file beside the newest installer, tests/fixtures/recorded/delugedownloads/<date>/*.json.
//
//   node scripts/recorded-replies/docker/deluge-downloads.mjs
//
// Real unauthenticated GETs from the public internet. The listing is kept whole (it is a few kilobytes).
import { Client, save } from './lib.mjs';

const day = new Date().toISOString().slice(0, 10);
const host = new Client('https://ftp.osuosl.org', { headers: { 'user-agent': 'Deluno-test-recording' }, keep: ['content-type'] });
const listing = await host.get('/pub/deluge/windows/');
const installers = [...listing.reply.bodyText.matchAll(/href="(deluge-[\d.]+-win64-setup\.exe)"/g)].map((m) => m[1]);
const newest = installers.sort((a, b) => a.localeCompare(b, undefined, { numeric: true })).at(-1);
if (!newest) throw new Error('the listing holds no installer');
const sum = await host.get(`/pub/deluge/windows/${newest}.sha256`);
const missing = await host.get(`/pub/deluge/windows/${newest.replace('win64', 'win-none')}.sha256`);
save('delugedownloads', day, 'windows-listing', 'The directory listing Deluno reads to find the newest Windows installer of Deluge (a plain Apache index, no account).', [listing]);
save('delugedownloads', day, 'installer-checksum', `The checksum file beside the newest installer (${newest}.sha256): a "hash  filename" line.`, [sum]);
save('delugedownloads', day, 'checksum-not-found', 'The reply for a checksum file the directory does not hold (the host\'s own 404 page).', [missing]);
console.log('done', listing.reply.status, sum.reply.status, missing.reply.status);
