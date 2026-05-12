# Deluno Troubleshooting Guide

Each section is self-contained. Find your symptom and follow the steps.

---

## ffprobe not found

**Symptom:** Import fails with "ffprobe was not found on PATH" or media probe status shows `unavailable`.

**Steps:**

1. On **Windows**: the installer bundles `ffprobe.exe` next to the binary. If it is missing, re-run the installer or copy `ffprobe.exe` into `%ProgramData%\Deluno\bin`.
2. On **Linux / Docker**: install ffmpeg in the container or on the host. Set the environment variable to the exact path:
   ```
   DELUNO_FFPROBE_PATH=/usr/bin/ffprobe
   ```
3. On **bare-metal Linux**: `sudo apt install ffmpeg` (or equivalent), then restart the service.
4. Confirm the path is correct: run `ffprobe -version` from a shell, or check the value of `DELUNO_FFPROBE_PATH` matches an existing file.
5. Restart Deluno after any change.

---

## Import pipeline failures

The import pipeline records a `kind` for each failure. Check **Activity** in the UI for the specific kind.

### `unsupportedFile`

The file extension is not in the allowed set (mkv, mp4, m4v, avi, mov, wmv, ts, m2ts). Rename the file to a supported extension or import a different release.

### `mediaProbeFailed`

ffprobe exited with a non-zero code or returned invalid JSON. The file may be corrupt or still being written by the download client.

1. Confirm the download is complete.
2. Play the file in a media player to verify it is not corrupt.
3. Check that Deluno can read the file (permissions).
4. Retry the import once the file is confirmed healthy.

### `replacementRejected`

Replacement protection blocked the import because the incoming file did not score higher than the existing file. See the "Replacement protection blocking a grab" section below.

### `permission`

Deluno cannot read the source or write to the destination.

1. Check that the user or service account running Deluno has read access to the download path.
2. Check write access to the library root.
3. On Docker: verify volume mounts expose both paths to the container.

### `io`

A read or write error occurred during the transfer.

1. Check disk space on both the source and destination volumes.
2. Check for network share connectivity if paths are on a NAS.
3. Check system logs for storage errors.

### `samePath`

Source and destination resolved to the same file. Deluno will not import a file onto itself.

1. Ensure the download client's completed path is separate from the library root.
2. Adjust the library root or naming rule so the destination differs from the source.

### `conflict`

The destination file already exists and overwrite was not enabled.

1. Open the import dialog and enable **Overwrite** if you intend to replace the existing file.
2. Otherwise, delete or move the existing file first.

---

## Download client not grabbing

**Symptom:** Deluno finds a result but nothing is sent to the download client.

1. Go to **Settings > Download Clients** and verify the client is reachable. Use the **Test** button.
2. Check **Settings > Libraries** — each library must have at least one download client linked.
3. Confirm the quality profile on the movie or series allows the release quality being considered.
4. Check **Activity** for a grab attempt. If an attempt appears but failed, read the error message for the specific reason (auth error, timeout, disk quota).
5. Verify the download client account has permission to add downloads and that the configured category exists.

---

## Indexer returning no results

**Symptom:** Manual search returns zero results for a title that exists on the indexer.

1. Go to **Settings > Indexers**, select the indexer, and click **Test**. A green result confirms connectivity and API key validity.
2. Confirm the API key is correct and has not been rotated on the indexer side.
3. Check the indexer's own web UI to confirm the title is indexed.
4. If using a proxy, verify it is not blocking the indexer host.
5. Check the indexer's category configuration — the configured categories must include Movies or TV as appropriate.
6. Review the Activity feed for indexer error messages during the last search attempt.

---

## Search not running automatically

**Symptom:** Wanted items are not being searched on schedule.

1. Go to **Settings > Libraries** and open the relevant library.
2. Confirm **Automation** is enabled for that library.
3. Check that a **Search Window** is configured (start and end time, or all-day).
4. Verify at least one indexer is enabled and passes the test.
5. Check **Activity** to see if a scheduled search cycle ran recently. If not, restart Deluno — the scheduler starts on application boot.

---

## Replacement protection blocking a grab

**Symptom:** A grab is rejected with `replacementRejected`.

Deluno scores the incoming release against the existing file. If the existing file scores equal or higher, the grab is blocked.

1. Open the movie or episode detail page.
2. Find the **Replacement Protection** or **Force Replace** option and disable it for this item if you want to allow a same-quality replacement.
3. Alternatively, confirm that the incoming release actually has a higher quality (resolution, codec, or audio) than the existing file — replacement protection is working as intended if it does not.

---

## Authentication issues

**Symptom:** Cannot log in, or locked out of the UI.

1. Confirm you are using the correct username and password set during bootstrap.
2. If the password is lost, reset via the bootstrap flow:
   - Stop Deluno.
   - Delete (or rename) the database file in `Storage__DataRoot` (`deluno.db`).
   - Restart Deluno — the UI returns to the bootstrap screen.
   - Set a new username and password.

   **Note:** deleting the database also removes all catalog data. Take a backup first if possible.
3. If using an API key and receiving 401 errors, regenerate the API key from **Settings > API Keys** and update any external tools that use it.

---

## Database corruption

**Symptom:** Application fails to start, crash on startup, or reports SQLite errors in logs.

1. Stop Deluno immediately to prevent further writes.
2. Locate the database file at `Storage__DataRoot/deluno.db`.
3. Run an integrity check: `sqlite3 deluno.db "PRAGMA integrity_check;"` — output should be `ok`.
4. If corrupt, restore from the most recent backup (see Backup and Restore in the deployment guide):
   - Upload the backup zip via `POST /api/backups/restore` before restarting, or place the database file from the backup archive directly at `Storage__DataRoot/deluno.db`.
5. Restart Deluno and verify the UI loads correctly.
6. If no backup exists, delete `deluno.db` and re-run the bootstrap flow. Catalog data will need to be re-added.
