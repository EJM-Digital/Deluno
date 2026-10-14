/**
 * The Files tab/section on the movie detail page (#782) — every file Deluno
 * can see on disk for this title, not only the one it tracks. A title with a
 * remux plus a sample previously had no way to see the second file at all.
 */
import { formatBytes } from "../../lib/utils";
import { useEffect, useState } from "react";
import { LoaderCircle, Pencil, Trash2 } from "lucide-react";
import { fetchJson, type MovieFileItem, type SubtitleLanguageOption } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Select } from "../ui/select";
import {
  ListCard,
  ListCell,
  ListEmpty,
  ListNameCell,
  ListRow,
  ListTable
} from "../ui/list-card";

function kindLabel(kind: MovieFileItem["kind"]) {
  return kind === "video" ? "Video" : kind === "subtitle" ? "Subtitle" : "Other";
}

export function MovieFilesCard({ movieId }: { movieId: string }) {
  const [files, setFiles] = useState<MovieFileItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [languageOptions, setLanguageOptions] = useState<SubtitleLanguageOption[]>([]);
  const [editingPath, setEditingPath] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState("");
  const [confirmingDeletePath, setConfirmingDeletePath] = useState<string | null>(null);
  const [busyPath, setBusyPath] = useState<string | null>(null);
  const [reloadVersion, setReloadVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    fetchJson<MovieFileItem[]>(`/api/movies/${movieId}/files`)
      .then((items) => { if (!cancelled) setFiles(items); })
      .catch(() => { if (!cancelled) setError("Could not read this title's files from disk."); });
    return () => { cancelled = true; };
  }, [movieId, reloadVersion]);

  useEffect(() => {
    let cancelled = false;
    fetchJson<SubtitleLanguageOption[]>("/api/subtitle-languages")
      .then((items) => { if (!cancelled) setLanguageOptions(items); })
      .catch(() => { /* the language select falls back to a bare text field below */ });
    return () => { cancelled = true; };
  }, []);

  function reload() {
    setReloadVersion((current) => current + 1);
  }

  function startEdit(item: MovieFileItem) {
    setConfirmingDeletePath(null);
    setEditingPath(item.path);
    setEditDraft(item.kind === "video" ? item.quality ?? "" : item.language ?? "");
  }

  async function saveEdit(item: MovieFileItem) {
    setBusyPath(item.path);
    try {
      if (item.kind === "video") {
        const response = await authedFetch(`/api/movies/${movieId}/files/quality`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ quality: editDraft })
        });
        if (!response.ok) throw new Error("quality-failed");
        toast.success("Quality updated.");
      } else {
        const response = await authedFetch(`/api/movies/${movieId}/files/language`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ path: item.path, language: editDraft, hearingImpaired: Boolean(item.hearingImpaired) })
        });
        if (!response.ok) throw new Error("language-failed");
        toast.success("Language updated.");
      }
      setEditingPath(null);
      reload();
    } catch {
      toast.error(item.kind === "video" ? "Could not update the quality." : "Could not update the language.");
    } finally {
      setBusyPath(null);
    }
  }

  async function confirmDelete(item: MovieFileItem) {
    setBusyPath(item.path);
    try {
      const response = await authedFetch(`/api/movies/${movieId}/files`, {
        method: "DELETE",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ path: item.path, libraryId: item.libraryId ?? undefined })
      });
      if (!response.ok) throw new Error("delete-failed");
      toast.success(
        item.isPrimary && item.libraryName
          ? `File moved to the recycle bin. Deluno will look for it again in ${item.libraryName}. You can restore it from the recycle bin.`
          : item.isPrimary ? "File moved to the recycle bin. This title now shows as missing and Deluno will look for it again. You can restore it from the recycle bin." : "File moved to the recycle bin. You can restore it from there.");
      setConfirmingDeletePath(null);
      reload();
    } catch {
      toast.error("Could not delete this file.");
    } finally {
      setBusyPath(null);
    }
  }

  // Quality and language edits act on the film's own tracked file, so only that copy offers them.
  const primaryLibraryId = files?.find((file) => file.isPrimary)?.libraryId ?? null;
  const count = files ? `${files.length} file${files.length === 1 ? "" : "s"}` : undefined;

  return (
    <ListCard title="Files" count={count}>
      {error ? (
        <ListEmpty title="Could not load files" description={error} />
      ) : files === null ? (
        <div className="flex items-center gap-2 px-(--card-pad-x) py-6 text-sm text-muted-foreground">
          <LoaderCircle className="h-4 w-4 animate-spin" /> Reading files from disk…
        </div>
      ) : files.length === 0 ? (
        <ListEmpty title="No files on disk" description="This title has no tracked file and nothing else was found in its folder." />
      ) : (
        <ListTable
          chevron={false}
          listId="movie-files"
          items={files}
          columns={[
            { id: "file", label: "File", sort: (item) => (item.path.split(/[\\/]/).pop() ?? item.path).toLowerCase() },
            { id: "kind", label: "Kind", mobile: true, sort: (item) => kindLabel(item.kind) },
            { id: "size", label: "Size", sort: (item) => item.sizeBytes },
            { id: "qualityLanguage", label: "Quality / language", mobile: true, sort: (item) => ((item.kind === "video" ? item.quality : item.language) ?? "").toLowerCase() },
            { id: "mediaInfo", label: "Media info", sort: (item) => [item.videoCodec, item.audioCodec].filter(Boolean).join(" · ").toLowerCase() },
            { id: "actions", label: "Actions", width: "220px", movable: false, sortable: false }
          ]}
        >
          {(rows) => rows.map((item) => {
            const editing = editingPath === item.path;
            const canEdit = (item.kind === "video" || item.kind === "subtitle") && (!item.libraryName || item.libraryId === primaryLibraryId);
            const fileName = item.path.split(/[\\/]/).pop() ?? item.path;
            return (
              <ListRow key={item.path}>
                <ListNameCell name={fileName} sub={[item.libraryName ? `In ${item.libraryName}` : null, item.isPrimary ? "Tracked file" : null].filter(Boolean).join(" · ") || undefined} />
                <ListCell primary={kindLabel(item.kind)} mobile />
                <ListCell primary={formatBytes(item.sizeBytes)} />
                <ListCell mobile>
                  {editing ? (
                    item.kind === "video" ? (
                      <Input
                        aria-label="Quality"
                        value={editDraft}
                        onChange={(event) => setEditDraft(event.target.value)}
                        placeholder="e.g. WEB 1080p"
                        className="h-8"
                      />
                    ) : (
                      <Select aria-label="Language" value={editDraft} onChange={(event) => setEditDraft(event.target.value)} className="h-8">
                        <option value="">Choose a language</option>
                        {languageOptions.map((language) => <option key={language.code} value={language.code}>{language.name}</option>)}
                      </Select>
                    )
                  ) : (
                    item.kind === "video" ? (item.quality ?? "Unknown") : (item.language ?? "Unknown")
                  )}
                </ListCell>
                <ListCell primary={[item.videoCodec, item.audioCodec].filter(Boolean).join(" · ") || "—"} />
                <ListCell>
                  <div className="flex items-center justify-end gap-1.5">
                    {editing ? (
                      <>
                        <Button type="button" size="sm" onClick={() => void saveEdit(item)} disabled={busyPath === item.path}>
                          {busyPath === item.path ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : "Save"}
                        </Button>
                        <Button type="button" size="sm" variant="ghost" onClick={() => setEditingPath(null)} disabled={busyPath === item.path}>
                          Cancel
                        </Button>
                      </>
                    ) : confirmingDeletePath === item.path ? (
                      <div className="flex min-w-0 flex-col items-end gap-2 text-right">
                        <p className="text-(length:--type-caption) leading-snug text-muted-foreground">Goes to the recycle bin. You can restore it. Deluno will look for it again.</p>
                        <div className="flex flex-wrap items-center justify-end gap-1.5">
                          <Button type="button" size="sm" variant="destructive" onClick={() => void confirmDelete(item)} disabled={busyPath === item.path}>
                            {busyPath === item.path ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : "Confirm delete"}
                          </Button>
                          <Button type="button" size="sm" variant="ghost" onClick={() => setConfirmingDeletePath(null)} disabled={busyPath === item.path}>
                            Cancel
                          </Button>
                        </div>
                      </div>
                    ) : (
                      <>
                        {canEdit ? (
                          <Button type="button" size="sm" variant="ghost" aria-label={`Edit ${fileName}${item.libraryName ? ` in ${item.libraryName}` : ""}`} onClick={() => startEdit(item)}>
                            <Pencil className="h-3.5 w-3.5" />
                          </Button>
                        ) : null}
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          className="text-destructive hover:bg-destructive/10 hover:text-destructive"
                          aria-label={`Delete ${fileName}${item.libraryName ? ` in ${item.libraryName}` : ""}`}
                          onClick={() => { setEditingPath(null); setConfirmingDeletePath(item.path); }}
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </Button>
                      </>
                    )}
                  </div>
                </ListCell>
              </ListRow>
            );
          })}
        </ListTable>
      )}
    </ListCard>
  );
}
