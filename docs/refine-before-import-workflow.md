# Refine before import workflow

Deluno supports two import workflows per library.

## Standard import

Use this for the normal Radarr/Sonarr-style path.

1. Deluno sends an approved release to an external download client.
2. The client downloads into its configured category/folder.
3. Deluno sees the item as import-ready.
4. Deluno previews the destination rule, imports the file, renames it, refreshes metadata, and records activity.

## Refine before import

Use this when a processor needs to clean the download before Deluno imports it. Examples include removing unwanted audio tracks, stripping subtitles, remuxing containers, or applying other file hygiene rules.

1. Deluno sends an approved release to an external download client.
2. The client finishes the download.
3. Deluno marks the item as waiting for the configured processor output.
4. The processor reads the original file and writes a clean file into the library's processor output folder.
5. The processor either reports status to `POST /api/integrations/processors/events` or writes the cleaned file into the configured clean output folder.
6. Deluno correlates the processor event/output folder file with the waiting download and queues the same import job used by standard imports.
7. Deluno imports the clean output file through the same destination resolver, mover/hardlink pipeline, naming rules, metadata refresh, and activity timeline as standard imports.

## Processor event contract

Endpoint:

```http
POST /api/integrations/processors/events
Authorization: Bearer deluno_...
Content-Type: application/json
```

Example:

```json
{
  "libraryId": "library-id",
  "mediaType": "movies",
  "entityType": "movie",
  "entityId": "movie-id-or-title",
  "sourcePath": "D:\\Downloads\\Movie.Release",
  "outputPath": "D:\\Deluno\\ProcessorOutput\\Movie.Release.clean.mkv",
  "status": "completed",
  "message": "Removed non-English audio and unwanted subtitles.",
  "processorName": "External Refiner"
}
```

Supported statuses:

- `accepted`
- `started`
- `completed`
- `failed`

## Output folder watcher

The processor event endpoint is preferred because it gives Deluno explicit status and correlation data. The output folder watcher exists as a recovery-friendly fallback for tools that can only write files.

Per library, Deluno can watch a configured clean output folder. When a supported video file appears there, Deluno:

1. checks whether that source path is already queued or imported
2. creates a destination preview using the library media type and naming rules
3. queues `filesystem.import.execute` with source `processor-output-watcher`
4. records `processing.output.import-queued` in Activity

If Deluno cannot read the output folder because a Docker volume, UNC share, mapped drive, or permission is wrong, it records `processing.output.scan-failed` instead of breaking the import worker.

## Timeout and manual review

Each refine library has a processor timeout. If a completed download waits longer than that timeout without a cleaned output, Deluno creates an import recovery case and records `processing.timeout`.

The recovery case tells the user what happened in plain language:

- check the processor logs and output folder
- retry once the cleaned file exists
- manually import the original only if that is the configured failure posture
- dismiss only when the user intentionally abandons the item

## UI wording

The app uses generic wording so this works with any trusted local processor now or later.

- Workflow: `Standard import`
- Workflow: `Refine before import`
- Queue status: `Processing`
- Settings label: `Clean output folder`
- Failure posture: `Stop and ask me`, `Send to manual review`, or `Import the original file`

## Product rule

Deluno does not make the processor mandatory. Standard import remains the default. The refined workflow is opt-in per library so users can run standard Movies, refined Movies, and refined TV from one Deluno install without needing multiple Arr instances.

## UX rule

The UI must always distinguish this workflow from a normal import. The user should be able to see:

- `Waiting for processor` while Deluno expects a cleaned output
- `Import queued` once the cleaned output has been detected
- `Import failed` when the import pipeline blocks the file
- the processor timeout recovery case in Queue/Activity

This keeps the advanced Refine workflow visible without making it feel like the default path.
