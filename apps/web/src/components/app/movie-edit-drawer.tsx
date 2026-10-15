/**
 * The movie detail page's Edit surface (#784) — separate from Edit metadata,
 * which only ever covered descriptive fields. This covers the four things
 * Radarr bundles into its own EditMovieModal and Deluno had scattered into
 * bulk-only tools: monitored state, quality profile, path (library/root,
 * with the same move-confirmation flow bulk reassignment uses), and tags.
 *
 * Every write goes through the existing bulk endpoints with a selection of
 * one - there is no separate single-title writer to keep in sync with the
 * bulk one.
 */
import { moveRefusalLines, type MoveResult } from "../../lib/move-refusals";
import { useEffect, useState } from "react";
import { LoaderCircle } from "lucide-react";
import { fetchJson, type LibraryItem, type QualityProfileItem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { useUnsavedChanges } from "../../hooks/use-unsaved-changes";
import { Drawer, DrawerFooter, DrawerSection, type DrawerSaveState } from "../ui/drawer";
import { Field } from "../ui/field";
import { CheckboxRow } from "../ui/checkbox";
import { Select } from "../ui/select";
import { Switch } from "../ui/switch";
import { TitleTagsEditor } from "./title-tags-editor";

interface MovieEditDrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  movieId: string;
  monitored: boolean;
  qualityProfileId: string | null;
  libraryId: string | null;
  filePath: string | null;
  metadataJson: string | null;
  /** Called after any write succeeds, so the page can revalidate. */
  onChanged: () => void;
}

export function MovieEditDrawer({
  open,
  onOpenChange,
  movieId,
  monitored,
  qualityProfileId,
  libraryId,
  filePath,
  metadataJson,
  onChanged
}: MovieEditDrawerProps) {
  const [formMonitored, setFormMonitored] = useState(monitored);
  const [formQualityProfileId, setFormQualityProfileId] = useState(qualityProfileId ?? "");
  const [targetLibraryId, setTargetLibraryId] = useState(libraryId ?? "");
  const [moveFiles, setMoveFiles] = useState(false);
  const [confirmingPathChange, setConfirmingPathChange] = useState(false);
  const [libraries, setLibraries] = useState<LibraryItem[]>([]);
  const [qualityProfiles, setQualityProfiles] = useState<QualityProfileItem[]>([]);
  const [optionsLoading, setOptionsLoading] = useState(false);
  const [state, setState] = useState<DrawerSaveState>("clean");
  const [message, setMessage] = useState<string | null>(null);
  // Each title Deluno refused to move, with its own reason (#1078). Stays until the next save.
  const [moveRefusals, setMoveRefusals] = useState<string[]>([]);

  const dirty = open && state === "dirty";
  useUnsavedChanges(dirty);

  // Reopening starts from what the server holds now, not from an abandoned edit.
  useEffect(() => {
    if (!open) return;
    setFormMonitored(monitored);
    setFormQualityProfileId(qualityProfileId ?? "");
    setTargetLibraryId(libraryId ?? "");
    setMoveFiles(false);
    setConfirmingPathChange(false);
    setState("clean");
    setMessage(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setOptionsLoading(true);
    Promise.all([
      fetchJson<LibraryItem[]>("/api/libraries"),
      fetchJson<QualityProfileItem[]>("/api/quality-profiles")
    ])
      .then(([libraryItems, profileItems]) => {
        if (cancelled) return;
        setLibraries(libraryItems.filter((item) => item.mediaType.toLowerCase() === "movies"));
        setQualityProfiles(profileItems.filter((item) => item.mediaType.toLowerCase() === "movies"));
      })
      .catch(() => {
        if (!cancelled) setMessage("Could not load libraries and quality profiles.");
      })
      .finally(() => {
        if (!cancelled) setOptionsLoading(false);
      });
    return () => { cancelled = true; };
  }, [open]);

  function edit() {
    setState("dirty");
    setMessage(null);
    setConfirmingPathChange(false);
  }

  const pathChanging = targetLibraryId !== "" && targetLibraryId !== (libraryId ?? "");

  async function handleSave() {
    // The same two-step confirmation the bulk tools dialog uses for
    // reassign-library: a path change never applies on the first click, so
    // files are never moved (or silently re-pointed) by a single click that
    // was meant for something else on the form.
    if (pathChanging && !confirmingPathChange) {
      setConfirmingPathChange(true);
      setMessage(
        moveFiles
          ? "Confirming will move this title's files to the new root."
          : "Confirming will reassign the record only — files stay where they are."
      );
      return;
    }

    setState("saving");
    setMessage(null);
    setMoveRefusals([]);
    try {
      if (formMonitored !== monitored) {
        const response = await authedFetch("/api/movies/monitoring", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ movieIds: [movieId], monitored: formMonitored })
        });
        if (!response.ok) throw new Error("monitoring-failed");
      }

      if (formQualityProfileId !== (qualityProfileId ?? "")) {
        const response = await authedFetch("/api/movies/bulk/quality-profile", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ movieIds: [movieId], qualityProfileId: formQualityProfileId.trim() || null })
        });
        if (!response.ok) throw new Error("quality-profile-failed");
      }

      let filesMoved = 0;
      let refusalLines: string[] = [];
      if (pathChanging && libraryId) {
        const response = await authedFetch("/api/movies/bulk/reassign-library", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            movieIds: [movieId],
            fromLibraryId: libraryId,
            toLibraryId: targetLibraryId,
            moveFiles
          })
        });
        if (!response.ok) throw new Error("reassign-failed");
        const result = await response.json() as MoveResult;
        filesMoved = result.filesMoved ?? 0;
        refusalLines = moveRefusalLines(result);
      }

      setState("saved");
      setMessage(
        refusalLines.length > 0
          ? "Saved, but Deluno did not move this title. The reason is below."
          : pathChanging && moveFiles && filesMoved > 0
            ? "Saved. The file was moved to the new root."
            : "Saved just now."
      );
      setMoveRefusals(refusalLines);
      setConfirmingPathChange(false);
      onChanged();
    } catch {
      setState("error");
      setMessage("Could not save these changes.");
    }
  }

  function requestClose() {
    onOpenChange(false);
  }

  return (
    <Drawer
      open={open}
      onOpenChange={(next) => { if (!next) requestClose(); else onOpenChange(true); }}
      title="Edit"
      description="Monitoring, quality profile, path and tags for this movie"
      onSubmit={(event) => { event.preventDefault(); void handleSave(); }}
      footer={
        <DrawerFooter
          state={state}
          message={message}
          // A message set before anything is edited ("3 matches found", "Could not load ...") is shown too (#1338).
          note={state === "clean" ? message : null}
          saveLabel={confirmingPathChange ? "Confirm and save" : "Save"}
          onCancel={requestClose}
          onSave={() => void handleSave()}
          disabled={optionsLoading}
        />
      }
    >
      <DrawerSection>
        <Field label="Monitored" help="Deluno keeps searching for an upgrade only while a title is monitored.">
          <div className="flex min-h-(--library-toolbar-height) items-center gap-2">
            <Switch checked={formMonitored} onCheckedChange={(checked) => { setFormMonitored(checked); edit(); }} />
            <span className="text-sm text-foreground">{formMonitored ? "Monitored" : "Unmonitored"}</span>
          </div>
        </Field>

        <Field label="Quality profile" help="The profile this title's own searches are held to.">
          {optionsLoading ? (
            <div className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="h-4 w-4 animate-spin" /> Loading…</div>
          ) : (
            <Select value={formQualityProfileId} onChange={(event) => { setFormQualityProfileId(event.target.value); edit(); }}>
              <option value="">Library default</option>
              {qualityProfiles.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
            </Select>
          )}
        </Field>

        <Field
          label="Path"
          help={filePath ? `Current file: ${filePath}` : "No file on disk yet."}
        >
          {optionsLoading ? (
            <div className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="h-4 w-4 animate-spin" /> Loading…</div>
          ) : (
            <Select value={targetLibraryId} onChange={(event) => { setTargetLibraryId(event.target.value); edit(); }}>
              {libraries.map((item) => <option key={item.id} value={item.id}>{item.name} — {item.rootPath}</option>)}
            </Select>
          )}
        </Field>

        {pathChanging ? (
          <Field label="Files" help={moveFiles ? "The file physically moves to the new library's root." : "Only the record moves; the file stays under the old root."}>
            <CheckboxRow label="Move the file to the new root" checked={moveFiles} onCheckedChange={(checked) => { setMoveFiles(checked); edit(); }} />
          </Field>
        ) : null}

        {confirmingPathChange ? (
          <div className="rounded-xl border border-warning/40 bg-warning/10 px-4 py-3 text-sm text-warning-foreground">
            {moveFiles
              ? "Confirming will move this title's file on disk to the new library's root."
              : "Confirming will reassign the record to the new library. The file stays exactly where it is."}
          </div>
        ) : null}

        {moveRefusals.length > 0 ? (
          <div role="alert" className="rounded-xl border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-foreground">
            <p className="font-semibold">Deluno did not move this title</p>
            <ul aria-label="Why Deluno did not move this title" className="mt-1 list-disc space-y-1 pl-4">
              {moveRefusals.map((line) => <li key={line}>{line}</li>)}
            </ul>
          </div>
        ) : null}

        <Field label="Tags" hideLabel>
          <TitleTagsEditor id={movieId} mediaType="movies" metadataJson={metadataJson} onSaved={onChanged} />
        </Field>
      </DrawerSection>
    </Drawer>
  );
}
