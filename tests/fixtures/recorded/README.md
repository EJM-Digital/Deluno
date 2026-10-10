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
var recording = RecordedReplies.LoadLatest("sabnzbd", "finished-single-file");
var client = new HttpClient(recording.ReplayHandler());          // answers by method and path
var body = recording.Find("mode=history").Reply.Body;             // or the raw reply text
```

A state that was never recorded fails with how to record it. Do not write the reply by hand to get
past that: say it is missing, and record it.

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
