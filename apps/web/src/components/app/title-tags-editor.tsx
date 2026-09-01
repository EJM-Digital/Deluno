import { LoaderCircle, Pencil, Plus, Save, Tag } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { fetchJson } from "../../lib/api";
import type { TagItem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { Button } from "../ui/button";
import { Input } from "../ui/input";

type TitleTagsMediaType = "movies" | "series";

/**
 * The title-level half of the tag feature. Bulk editing already has the same
 * endpoint; this keeps the single-title path on that contract rather than
 * inventing a second metadata writer.
 */
export function TitleTagsEditor({
  id,
  mediaType,
  metadataJson,
  onSaved
}: {
  id: string;
  mediaType: TitleTagsMediaType;
  metadataJson: string | null;
  onSaved?: () => void;
}) {
  const tagsFromMetadata = useMemo(() => readTags(metadataJson), [metadataJson]);
  const [tags, setTags] = useState(tagsFromMetadata);
  const [draft, setDraft] = useState(tagsFromMetadata.join(", "));
  const [tagOptions, setTagOptions] = useState<string[]>([]);
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setTags(tagsFromMetadata);
    setDraft(tagsFromMetadata.join(", "));
    setEditing(false);
  }, [tagsFromMetadata]);

  useEffect(() => {
    let cancelled = false;
    const segment = mediaType === "movies" ? "movies" : "series";
    void fetchJson<TagItem[]>("/api/tags")
      .then((items) => {
        if (!cancelled) setTagOptions(items.map((item) => item.name).filter(Boolean).sort((a, b) => a.localeCompare(b)));
      })
      .catch(() => {
        // Tags are optional suggestions. The free-form editor still works when
        // the platform catalogue is temporarily unavailable.
      });

    // Tags are catalogue relationships, not provider metadata. Keep the
    // metadata read as a compatibility fallback for an older server, but let
    // the canonical endpoint win whenever it is available.
    void fetchJson<Array<{ tagId: string; name: string }>>(`/api/${segment}/${id}/tags`)
      .then((items) => {
        if (cancelled) return;
        const next = normalizeTags(items.map((item) => item.name).join(","));
        setTags(next);
        setDraft(next.join(", "));
      })
      .catch(() => {
        // The metadata fallback above keeps the editor usable during a staged
        // deployment where the catalogue endpoint is not available yet.
      });

    return () => { cancelled = true; };
  }, [id, mediaType]);

  async function save() {
    setSaving(true);
    setError(null);
    try {
      const nextTags = normalizeTags(draft);
      const body = mediaType === "movies"
        ? { movieIds: [id], tags: nextTags.join(", ") }
        : { seriesIds: [id], tags: nextTags.join(", ") };
      const response = await authedFetch(`/api/${mediaType}/bulk/tags`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      if (!response.ok) throw new Error("Tags could not be saved.");
      setTags(nextTags);
      setDraft(nextTags.join(", "));
      setEditing(false);
      onSaved?.();
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : "Tags could not be saved.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="mt-3 flex flex-wrap items-center gap-2 rounded-xl border border-white/10 bg-card/55 px-3 py-2 backdrop-blur-sm">
      <span className="flex items-center gap-1.5 text-[length:var(--type-micro)] font-bold uppercase tracking-[0.14em] text-muted-foreground">
        <Tag className="h-3.5 w-3.5" aria-hidden="true" />
        Tags
      </span>
      {editing ? (
        <>
          <Input
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            list={`title-tags-${id}`}
            aria-label="Tags"
            placeholder="Add tags, comma-separated"
            className="h-8 min-w-[14rem] flex-1 bg-background/60 text-sm"
            autoFocus
          />
          <datalist id={`title-tags-${id}`}>
            {tagOptions.map((tag) => <option key={tag} value={tag} />)}
          </datalist>
          <Button type="button" size="sm" onClick={() => void save()} disabled={saving}>
            {saving ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />}
            Save
          </Button>
          <Button type="button" size="sm" variant="ghost" onClick={() => { setDraft(tags.join(", ")); setEditing(false); }} disabled={saving}>
            Cancel
          </Button>
        </>
      ) : (
        <>
          {tags.length ? tags.map((tag) => (
            <span key={tag} className="rounded-full border border-primary/20 bg-primary/10 px-2 py-0.5 text-xs font-medium text-primary">{tag}</span>
          )) : <span className="text-xs text-muted-foreground">None assigned</span>}
          <Button type="button" size="sm" variant="ghost" className="ml-auto" onClick={() => setEditing(true)}>
            {tags.length ? <Pencil className="h-3.5 w-3.5" /> : <Plus className="h-3.5 w-3.5" />}
            {tags.length ? "Edit" : "Add"}
          </Button>
        </>
      )}
      {error ? <span role="alert" className="basis-full text-xs text-destructive">{error}</span> : null}
    </div>
  );
}

function readTags(metadataJson: string | null) {
  if (!metadataJson) return [] as string[];
  try {
    const parsed = JSON.parse(metadataJson) as Record<string, unknown>;
    const entry = Object.entries(parsed).find(([key]) => key.toLowerCase() === "tags")?.[1];
    if (Array.isArray(entry)) return normalizeTags(entry.filter((item): item is string => typeof item === "string").join(","));
    return typeof entry === "string" ? normalizeTags(entry) : [];
  } catch {
    return [];
  }
}

function normalizeTags(raw: string) {
  return raw
    .split(/[;,\n\r]/)
    .map((tag) => tag.trim())
    .filter(Boolean)
    .filter((tag, index, all) => all.findIndex((candidate) => candidate.toLowerCase() === tag.toLowerCase()) === index);
}
