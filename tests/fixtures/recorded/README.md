# Recorded replies (#1237)

The real replies of the apps Deluno talks to, captured from the real apps. A test of a download
client, indexer, media server or processor (Weir) loads its replies from here. It never builds a
reply in the test: a shape written from memory kept passing while the real apps behaved differently
(#1212, #1225, #1221).

```
tests/fixtures/recorded/<app>/<version>/<state>.json
```

`<app>` is `sabnzbd`, `nzbget`, `qbittorrent`, `transmission`, `deluge`, `weir`, and `deluno` (Deluno's
own side of its talk with Weir, which Weir's tests replay). `<version>` is what the app reports.
`<state>` is one situation, for example `queued`, `downloading`, `stalled`, `finished-folder`,
`removed-twice`, `handoff-ended`.

```json
{
  "app": "sabnzbd",
  "version": "5.1.3",
  "state": "finished-single-file",
  "recordedAt": "2026-10-12T09:00:00Z",
  "note": "what was done to the app to reach this state",
  "exchanges": [
    {
      "request": { "method": "GET", "path": "/api?mode=history&output=json", "body": null },
      "reply": { "status": 200, "headers": { "content-type": "application/json" }, "body": { } }
    }
  ]
}
```

A JSON reply is stored in `body`. A reply that is not JSON, or one whose exact text matters, is stored
in `bodyText`. Requests carry `body` or `bodyText` the same way, with secrets removed.

## Using a recording

C# tests link `tests/fixtures/RecordedReplies.cs` (the three test projects already do):

```csharp
// Answer each call with the recorded reply to the same KIND of call (SABnzbd's mode and name, a JSON-RPC method, qBittorrent's path).
// The ids in the call are the test's own; the reply is what the real app said.
var handler = RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("removed-without-data"), RecordedReplies.Sabnzbd("removed-with-data")]);

// One recorded reply as an HTTP response, or as text.
var response = RecordedReplies.Qbittorrent("finished-single-file").Response("torrents/info");
var body = RecordedReplies.Deluge("finished-single-file").Body("web.update_ui");

// The recorded reply with only the values the test is about changed. A state the VM cannot be put in (a qBittorrent torrent
// in "metaDL", a ratio) is the nearest recorded state with that one value set; everything else stays as the app sent it.
var info = RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info", ("0.state", "metaDL"), ("0.progress", 0.4));
```

A path in `Body`/`Response` is dot-separated names and array positions; `*` is the first property of an object (a
Deluge torrent is keyed by its hash); a null value removes the property. A state that was never recorded fails with how to
record it. Do not write the reply by hand to get past that: say it is missing, and record it.

Replies are the newest version recorded of each app (`MISSING.md` lists what the VM could not produce). rTorrent, uTorrent and the
media servers are not recorded; a test of those keeps its own shape and says so in `tests/recorded-reply-allow-list.json`.

## Recording

`scripts/recorded-replies/Invoke-RecordReplies.ps1 -RigHost <host>` drives each app on the golden VM
through its states and writes this folder; see `scripts/recorded-replies/README.md`. Everything it
writes goes through `scripts/recorded-replies/scrub.mjs`, which removes API keys, passwords, cookies,
LAN addresses and machine names. `RecordedRepliesTests` fails if a committed recording still carries
any of them.

## Deliberate malformed replies

A test that needs a reply no real app would send (truncated JSON, the wrong type) may build it inline
only if it is listed, with a reason, in `tests/recorded-reply-allow-list.json`. `npm run ci:check` runs
`scripts/check-recorded-replies.mjs`, which fails on any other test that builds an external app's reply.
