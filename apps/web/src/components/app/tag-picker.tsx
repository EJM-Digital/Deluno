import { Plus, X } from "lucide-react";
import { useCallback, useEffect, useId, useMemo, useRef, useState } from "react";
import { fetchJson } from "../../lib/api";
import type { TagItem, TagUsageItem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { cn } from "../../lib/utils";
import { Input } from "../ui/input";

/** Tag names from a stored comma-separated string: split, trimmed, no repeats (case-insensitive). */
export function parseTagList(raw: string | null | undefined): string[] {
  return (raw ?? "")
    .split(/[;,\n\r]/)
    .map((tag) => tag.trim())
    .filter(Boolean)
    .filter((tag, index, all) => all.findIndex((candidate) => candidate.toLowerCase() === tag.toLowerCase()) === index);
}

export function joinTagList(tags: string[]): string {
  return tags.join(", ");
}

const key = (name: string) => name.trim().toLowerCase();

/**
 * The one tag picker (#1042). Every place that takes tags uses it, so a typo
 * can no longer quietly create a tag that nothing carries: pick an existing tag,
 * or create a new one with an explicit button.
 *
 * The value stays the string each screen already stores (comma-separated names,
 * or one name in `single` mode); only the way it is edited changes.
 */
export function TagPicker({
  value,
  onChange,
  label = "Tags",
  single = false,
  disabled = false,
  warnUnused = true,
  placeholder,
  id
}: {
  value: string;
  onChange: (value: string) => void;
  /** Names this picker for screen readers and its buttons; keep it unique on a screen. */
  label?: string;
  /** Exactly one tag or none (a rule that matches one tag). */
  single?: boolean;
  disabled?: boolean;
  /** Say so beside the field when a chosen tag is on no title. Off where the picker is putting tags on titles. */
  warnUnused?: boolean;
  placeholder?: string;
  id?: string;
}) {
  const generatedId = useId();
  const inputId = id ?? generatedId;
  const [tags, setTags] = useState<TagItem[]>([]);
  const [usage, setUsage] = useState<TagUsageItem[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [draft, setDraft] = useState("");
  const [open, setOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    void Promise.all([
      fetchJson<TagItem[]>("/api/tags").catch(() => null),
      fetchJson<TagUsageItem[]>("/api/tags/usage").catch(() => [] as TagUsageItem[])
    ]).then(([tagItems, usageItems]) => {
      if (cancelled) return;
      if (tagItems) setTags(tagItems);
      setUsage(usageItems);
      // Without the list we cannot tell a good tag from a bad one, so say nothing.
      setLoaded(tagItems !== null);
    });
    return () => { cancelled = true; };
  }, []);

  const selected = useMemo(() => parseTagList(value), [value]);
  const selectedKeys = useMemo(() => new Set(selected.map(key)), [selected]);
  const usageByName = useMemo(() => new Map(usage.map((item) => [key(item.name), item])), [usage]);
  const knownNames = useMemo(() => new Set(tags.map((tag) => key(tag.name))), [tags]);

  const trimmed = draft.trim();
  const matches = useMemo(
    () => tags
      .filter((tag) => !selectedKeys.has(key(tag.name)) && key(tag.name).includes(key(trimmed)))
      .sort((a, b) => a.name.localeCompare(b.name))
      .slice(0, 8),
    [tags, selectedKeys, trimmed]
  );
  const exactExists = trimmed !== "" && (knownNames.has(key(trimmed)) || selectedKeys.has(key(trimmed)));

  const commit = useCallback((next: string[]) => onChange(joinTagList(next)), [onChange]);

  function pick(name: string) {
    setError(null);
    commit(single ? [name] : [...selected, name]);
    setDraft("");
    setOpen(false);
  }

  function remove(name: string) {
    commit(selected.filter((tag) => key(tag) !== key(name)));
  }

  async function create() {
    if (!trimmed || creating) return;
    setCreating(true);
    setError(null);
    try {
      const response = await authedFetch("/api/tags", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: trimmed, color: "slate", description: "" })
      });
      if (!response.ok) throw new Error("The tag could not be created.");
      const created = (await response.json()) as TagItem;
      setTags((current) => [...current, created]);
      pick(created.name || trimmed);
    } catch (createError) {
      setError(createError instanceof Error ? createError.message : "The tag could not be created.");
    } finally {
      setCreating(false);
    }
  }

  const unusedNames = loaded && warnUnused
    ? selected.filter((name) => (usageByName.get(key(name))?.totalCount ?? 0) === 0)
    : [];
  const showInput = !(single && selected.length > 0);
  const titleCount = (name: string) => usageByName.get(key(name))?.totalCount ?? 0;

  return (
    <div
      ref={rootRef}
      className="grid gap-2"
      onBlur={(event) => { if (!rootRef.current?.contains(event.relatedTarget as Node | null)) setOpen(false); }}
    >
      {selected.length > 0 ? (
        <ul className="flex flex-wrap gap-1.5" aria-label={`${label} chosen`}>
          {selected.map((name) => {
            const unused = unusedNames.includes(name);
            return (
              <li
                key={key(name)}
                className={cn(
                  "inline-flex items-center gap-1 rounded-full border py-0.5 pl-2.5 pr-1 text-[length:var(--type-caption)] font-medium",
                  unused ? "border-warning/40 bg-warning/10 text-warning" : "border-primary/20 bg-primary/10 text-primary"
                )}
              >
                <span>{name}</span>
                <button
                  type="button"
                  disabled={disabled}
                  aria-label={`Remove ${name} from ${label}`}
                  onClick={() => remove(name)}
                  className="rounded-full p-0.5 hover:bg-foreground/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40"
                >
                  <X className="h-3 w-3" aria-hidden="true" />
                </button>
              </li>
            );
          })}
        </ul>
      ) : null}
      {showInput ? (
        <div className="relative">
          <Input
            id={inputId}
            value={draft}
            disabled={disabled}
            autoComplete="off"
            placeholder={placeholder ?? (single ? "Choose a tag" : "Choose or create a tag")}
            aria-label={label}
            onFocus={() => setOpen(true)}
            onClick={() => setOpen(true)}
            onChange={(event) => { setDraft(event.target.value); setOpen(true); setError(null); }}
            onKeyDown={(event) => {
              if (event.key === "Escape") setOpen(false);
              if (event.key === "Enter") {
                event.preventDefault();
                // Enter only ever picks an existing tag. Creating one is its own button.
                const exact = tags.find((tag) => key(tag.name) === key(trimmed) && !selectedKeys.has(key(tag.name)));
                if (exact) pick(exact.name);
                else if (matches.length === 1) pick(matches[0]!.name);
              }
            }}
          />
          {open && !disabled && (matches.length > 0 || (trimmed !== "" && !exactExists)) ? (
            <div className="absolute z-20 mt-1 grid w-full gap-0.5 rounded-[12px] border border-hairline bg-surface-1 p-1 shadow-lg" role="group" aria-label={`${label} choices`}>
              {matches.map((tag) => (
                <button
                  key={tag.id}
                  type="button"
                  aria-label={`Add ${tag.name} to ${label}`}
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => pick(tag.name)}
                  className="flex items-center justify-between rounded-[8px] px-2.5 py-1.5 text-left text-[length:var(--type-body-sm)] text-foreground hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                >
                  <span>{tag.name}</span>
                  <span className="text-[length:var(--type-caption)] text-muted-foreground">
                    {titleCount(tag.name) === 0 ? "No titles yet" : `${titleCount(tag.name)} ${titleCount(tag.name) === 1 ? "title" : "titles"}`}
                  </span>
                </button>
              ))}
              {trimmed !== "" && !exactExists ? (
                <button
                  type="button"
                  disabled={creating}
                  aria-label={`Create new tag ${trimmed} for ${label}`}
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => void create()}
                  className="flex items-center gap-2 rounded-[8px] px-2.5 py-1.5 text-left text-[length:var(--type-body-sm)] font-medium text-primary hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50"
                >
                  <Plus className="h-3.5 w-3.5" aria-hidden="true" />
                  Create new tag &ldquo;{trimmed}&rdquo;
                </button>
              ) : null}
            </div>
          ) : null}
        </div>
      ) : null}
      {error ? <p role="alert" className="text-[length:var(--type-caption)] text-destructive">{error}</p> : null}
      {unusedNames.length > 0 ? (
        <p role="status" className="text-[length:var(--type-caption)] text-warning">
          {unusedNames.map((name) => `"${name}"`).join(", ")} {unusedNames.length === 1 ? "is" : "are"} not on any title yet, so nothing matches {unusedNames.length === 1 ? "it" : "them"} until a title is given {unusedNames.length === 1 ? "that tag" : "those tags"}.
        </p>
      ) : null}
    </div>
  );
}
