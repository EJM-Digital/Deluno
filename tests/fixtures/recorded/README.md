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
Deluge torrent is keyed by its hash); a null value removes the property (`RecordedReplies.JsonNull` sets an explicit JSON null). `[Name=InterDir]`
picks the list element whose `Name` is `InterDir` (NZBGet's config is a list of `{Name, Value}`; the dots inside the brackets
belong to the name). A path starting `>` renames a property (a category or label Deluno made is scrubbed in a recording, and the
test needs the name its code looks for); `^list` with a count keeps that many elements, and `+list` with a count copies the
first element up to that many (`+` alone is the list at the root). A state that was never recorded fails with how to
record it. Do not write the reply by hand to get past that: say it is missing, and record it.

```csharp
// A sequence as it happened (pause, list, resume, list): each call gets the next recorded reply to the same kind of call, the last repeats.
// The recordings given after answer the calls the sequence never made (the sign-in before it).
var handler = RecordedReplies.Qbittorrent("resumed").Sequence(RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"));

// An app that did not answer: every request fails with the connection error the recording holds.
var down = RecordedReplies.Deluge("not-answering").NotAnswering();
```

Replies are the newest version recorded of each app (`MISSING.md` lists what the VM could not produce). rTorrent, uTorrent and the
media servers are not recorded; a test of those keeps its own shape and says so in `tests/recorded-reply-allow-list.json`.

## Recording

`scripts/recorded-replies/Invoke-RecordReplies.ps1 -RigHost <host>` drives each app on the golden VM
through its states and writes this folder; see `scripts/recorded-replies/README.md`. Everything it
writes goes through `scripts/recorded-replies/scrub.mjs`, which removes API keys, passwords, cookies,
LAN addresses and machine names. `RecordedRepliesTests` fails if a committed recording still carries
any of them.

## Every recording is replayed

`npm run ci:check` also fails for a recording that no test replays (the newest version of each app): a test names a state by
loading it (`RecordedReplies.Qbittorrent("paused")`), or lists it in a file that loads that app's recordings. A state Deluno has no
behaviour for (a reply to a call it never makes, such as moving an existing Usenet job to another category) is listed under
`noBehaviour` in `tests/recorded-reply-allow-list.json` with the reason.

## uTorrent and rTorrent

Neither is installed in the golden VM, so their tests still build their replies inline and are listed `pending` in the allow-list,
each entry saying exactly what to record for that file (the token page, `list=1` rows, the `action=` replies and a wrong login for
uTorrent; `system.client_version`, `d.multicall2`, the pause, resume and erase calls and their fault for an unknown hash, the load
calls, label, files, trackers and a wrong login for rTorrent). The check flags an inline uTorrent or rTorrent reply the same way as any other.

## Deliberate malformed replies

A test that needs a reply no real app would send (truncated JSON, the wrong type) may build it inline
only if it is listed, with a reason, in `tests/recorded-reply-allow-list.json`. `npm run ci:check` runs
`scripts/check-recorded-replies.mjs`, which fails on any other test that builds an external app's reply.

## Controls, wrong logins, configuration errors and Weir's states (recorded 13 Oct 2026)

Recorded with `Invoke-RecordReplies.ps1 -Extra controls | nzbget-config | weir-states` (`scripts/recorded-replies/README.md`).

| App | States |
|---|---|
| SABnzbd 5.1.3, NZBGet 26.3 | `resumed`: a job paused by its id and resumed by its id, with the queue before and after. |
| qBittorrent 5.2.1, Transmission 4.1.3, Deluge 2.2.0 | `resumed` (pause, list, resume, list) and `pause-unknown-id` (a hash no client ever had; Transmission also a numeric id). qBittorrent answers 200 and nothing; Transmission answers success; Deluge answers an error with a Python traceback. |
| qBittorrent | `api-key-refused`: a bad API key sent as a bearer token is a 403 "Forbidden". |
| Transmission | `login-refused`: the 401, its `WWW-Authenticate: Basic realm="Transmission"` and body. The VM's Transmission has RPC authentication off, so this came from a second copy of the same daemon started for a few seconds with a throwaway login. |
| Deluge | `unauthenticated-call`: after a failed `auth.login` (result false), every call answers `Not authenticated` (code 1). |
| NZBGet | `login-refused` (replaced: it held only the open `version` call): `listgroups`, `status` and `version` with a wrong login, and with none, all answer 401 with `WWW-Authenticate: Basic realm="NZBGet"`. NZBGet lets `AuthorizedIP` (127.0.0.1) in without a login, so this was recorded with that option emptied for a few seconds. |
| NZBGet | `loadconfig-clean`, `config-error-paused` (`reload` after an invalid `AppBin=x` line, then `status` with `DownloadPaused` true and `log` [0, 200] with "Invalid option" and "Pausing all activities due to errors in configuration"), `config-error-cleared` (the line gone, `reload`, `DownloadPaused` false with no `resumedownload` needed). `loadconfig` is the older file taken from a config that already carried the four invalid lines (#1257). |
| Weir 1.0.0-rc.13 | `handoff-release-folder-accepted` and `release-folder-status-working/completed`; `handoff-source-files-accepted` and `source-files-status-working/completed` (Weir treats `sourceFiles` like the folder: it answers the same); `status-queued` (with `queuePosition`), `status-working`, `status-cancelled`, `status-failed` (a file under the 50 MB minimum); `outcome-handoff-not-finished-queued` and `-working` (409 `handoff_not_finished`); `outcome-handoff-ended` (after a cancel) and `outcome-handoff-ended-after-failed` (409 `handoff_ended`); `handoff-cancelled` (204 for a queued hand-off), `handoff-cancel-working` (409 "is working") and `handoff-cancel-too-late` (409 "is completed"); `outcome-not-imported-no-reason`, `-default-reason` and `-import-refused`, and `outcome-imported-with-reason`; `outcome-superseded` and `outcome-superseded-newer`. |

Weir accepts any `reason` text on a `not-imported` outcome and answers every one the same (`released` false, "kept its copy"), so the reasons recorded are the three kinds Deluno sends: none, the default words of a closed hand-off, and an import refusal. Weir has no separate "superseded" answer either: the same source sent again gets a new hand-off, and the outcome of the older one is answered like any other (200).

Two older Weir recordings were misnamed and were renamed to what they hold: `outcome-before-finished-200` is `outcome-finished-in-burst-200` (a 200 for a hand-off that had already finished), and `handoff-cancel-too-late` is `handoff-cancelled-in-burst-204` (a 204 for a hand-off still queued). The true "before finished" and "too late" replies are the `outcome-handoff-not-finished-*` and `handoff-cancel-too-late` states above.

## Deluno's own side, and Weir's refusals (recorded 13 Oct 2026)

`deluno/1.0.0-rc.30.30/` holds what the real Deluno sent Weir and what Weir sent Deluno, written down by `Start-RecordingProxy.ps1` (one proxy in front of Weir, one in front of Deluno, with Deluno's connection to Weir and Weir's connection to Deluno pointed at them for the run and put back after). A request carries `headers`: `x-webhook-secret` (scrubbed), `idempotency-key` (`<handoffId>:<revision>`) and `content-type`. The states are in `docs/deluno-weir-contract.md`, sections 7 and 8.

`weir/1.0.0-rc.13/` gained Weir's refusals, sent straight at Weir with a wrong or no secret or a refused body (`status-wrong-secret`, `status-no-secret`, `cancel-wrong-secret`, `outcome-wrong-secret`, `cancel-unknown-id`, `status-unknown-id`, `status-malformed-id`, `outcome-unknown-id`, `cancel-completed`, `status-unknown-source`, `status-no-connection-for-source`, `outcome-malformed`, `origin-without-requested-with`). These requests carry no `headers` (this recorder does not keep them): the wrong-secret ones were sent with a wrong value and the no-secret one with none; `status-unknown-source` holds the same call with the secret and then without it. `outcome-malformed` holds five calls in order: an unknown outcome word, a missing outcome, text that is not JSON (all `422`), the first of these again without a secret (`422`: the body is checked before the secret), and a body not declared as JSON (`415`).

### Deluno's replies to Weir's calls, and the manifest (rc.30.32)

`deluno/1.0.0-rc.30.32/reply-*.json` hold Deluno's own replies to the four calls Weir makes of it (the hand-off list, where downloads land, import the original, send the outcome again), recorded from the server code through the in-process test host, not from the golden VM: `DelunoRepliesToWeirTests` makes the calls, writes each file when run with `DELUNO_RECORD_REPLIES=1`, and otherwise holds the live reply to the file member by member. Generated ids, times and the test's data folder are replaced by `<id>`, `<utc>` and `<data-root>`. `RecordedReplies.Deluno(state)` loads a state from the newest version that holds it, so the older `rc.30.30` states are still current. `deluno/MANIFEST.sha256` lists a hash of every file there; `scripts/check-recorded-manifest.mjs` fails when a recording changes without it (`--write` rewrites it), and the Weir session copies the folder and the manifest. The cases still missing are in `MISSING.md`.

### Where Weir puts a single-file source's output

Recorded from the real Deluno handing Weir three downloads (`real-single-file-status-completed`, `real-release-folder-status-completed`, `real-usenet-folder-status-completed`):

| Source Deluno hands over | `outputPath` Weir reports |
|---|---|
| a single file, `C:\Downloads\Completed\Movies\<release>.mkv` | `C:\Weir\Ready\Movies\<release>.mkv`: **flat**, no folder named after the source |
| a release folder (Transmission), `C:\Downloads\Completed\Movies\<release>` | `C:\Weir\Ready\Movies\<release>\<release>.mkv` |
| a Usenet job: Deluno names the file inside the job folder, `...\Movies\<release>\<release>.mkv` | `C:\Weir\Ready\Movies\<release>\<release>.mkv` |

So the output is at `<output>\<path of the source relative to the watched folder>`, and a single file has no stem folder: `WorkPlanner.FindCorrelatedProcessorOutputs`, which looks only in a folder named after the source, cannot find a single-file output on its own (Deluno #1262). The events report carries the real path, so an import that follows the report is not affected.
