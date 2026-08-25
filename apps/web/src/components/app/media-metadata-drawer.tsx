/**
 * The one metadata editor, shared by the movie and TV detail pages.
 *
 * It replaced two different surfaces doing the same four jobs: a `<details>`
 * block on the show page and a max-w-5xl Radix dialog on the movie page. Both
 * searched the provider, linked a match, refreshed, and wrote local overrides.
 *
 *   Provider record  — what Deluno is using now, plus Refresh
 *   Correct the match — search the provider and link the right title
 *   Fine-tune         — the manual override fields, saved by the footer
 */
import { useEffect, useState } from "react";
import { ExternalLink, LoaderCircle, RefreshCw, Search } from "lucide-react";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { Disclosure } from "../ui/disclosure";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection, type DrawerSaveState } from "../ui/drawer";
import { Field, FieldRow } from "../ui/field";
import { Input } from "../ui/input";
import { Textarea } from "../ui/textarea";
import { fetchJson, type MetadataSearchResult } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { useUnsavedChanges } from "../../hooks/use-unsaved-changes";

export interface MediaMetadataValue {
  originalTitle: string;
  overview: string;
  posterUrl: string;
  backdropUrl: string;
  rating: string;
  genres: string;
  externalUrl: string;
  imdbId: string;
}

interface MediaMetadataDrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** `/api/series/{id}` or `/api/movies/{id}` — the four calls hang off this. */
  endpointBase: string;
  mediaType: "movies" | "tv";
  /** "series" or "movie", used in the sentences the drawer writes. */
  mediaLabel: string;
  title: string;
  year: number | null;
  provider: string | null;
  providerId: string | null;
  posterUrl: string | null;
  externalUrl: string | null;
  value: MediaMetadataValue;
  /** Called after anything that changes what the server holds. */
  onChanged: () => void;
}

export function MediaMetadataDrawer({
  open,
  onOpenChange,
  endpointBase,
  mediaType,
  mediaLabel,
  title,
  year,
  provider,
  providerId,
  posterUrl,
  externalUrl,
  value,
  onChanged
}: MediaMetadataDrawerProps) {
  const [form, setForm] = useState<MediaMetadataValue>(value);
  const [query, setQuery] = useState(title);
  const [matches, setMatches] = useState<MetadataSearchResult[]>([]);
  const [searched, setSearched] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [state, setState] = useState<DrawerSaveState>("clean");
  const [message, setMessage] = useState<string | null>(null);
  const [fineTuneOpen, setFineTuneOpen] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);

  const dirty = open && state === "dirty";
  useUnsavedChanges(dirty);

  // Reopening starts from what the server holds now, not from an abandoned edit.
  useEffect(() => {
    if (!open) return;
    setForm(value);
    setQuery(title);
    setMatches([]);
    setSearched(false);
    setState("clean");
    setMessage(null);
    setFineTuneOpen(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  function edit(patch: Partial<MediaMetadataValue>) {
    setForm((current) => ({ ...current, ...patch }));
    setState("dirty");
    setMessage(null);
  }

  async function handleSearch() {
    setBusy("search");
    setSearched(true);
    setMessage(null);
    try {
      const params = new URLSearchParams({ query: query.trim() || title, mediaType });
      if (year) params.set("year", String(year));
      const results = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      setMatches(results.slice(0, 6));
      if (state === "clean") {
        setMessage(results.length ? `${results.length} match${results.length === 1 ? "" : "es"} found` : "No matches found");
      }
    } catch {
      setState("error");
      setMessage("Provider search failed");
    } finally {
      setBusy(null);
    }
  }

  async function handleLink(match: MetadataSearchResult) {
    setBusy(`link:${match.providerId}`);
    setMessage(null);
    try {
      const response = await authedFetch(`${endpointBase}/metadata/link`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ providerId: match.providerId })
      });
      if (!response.ok) throw new Error("link-failed");
      setMatches([]);
      setState("saved");
      setMessage(`Linked to ${match.title}${match.year ? ` (${match.year})` : ""}`);
      onChanged();
    } catch {
      setState("error");
      setMessage("That match could not be linked");
    } finally {
      setBusy(null);
    }
  }

  async function handleRefresh() {
    setBusy("refresh");
    setMessage(null);
    try {
      const response = await authedFetch(`${endpointBase}/metadata/refresh`, { method: "POST" });
      if (!response.ok) throw new Error("refresh-failed");
      setState("saved");
      setMessage("Metadata refreshed");
      onChanged();
    } catch {
      setState("error");
      setMessage("Metadata refresh failed");
    } finally {
      setBusy(null);
    }
  }

  async function handleSave() {
    const invalid = validate(form);
    if (invalid) {
      setState("error");
      setMessage(invalid);
      setFineTuneOpen(true);
      return;
    }

    setState("saving");
    setMessage(null);
    try {
      const response = await authedFetch(`${endpointBase}/metadata/override`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          originalTitle: form.originalTitle.trim() || null,
          overview: form.overview.trim() || null,
          posterUrl: form.posterUrl.trim() || null,
          backdropUrl: form.backdropUrl.trim() || null,
          rating: form.rating.trim() ? Number(form.rating) : null,
          genres: form.genres.trim() || null,
          externalUrl: form.externalUrl.trim() || null,
          imdbId: form.imdbId.trim() || null
        })
      });
      if (!response.ok) throw new Error("override-failed");
      setState("saved");
      setMessage("Saved just now");
      onChanged();
    } catch {
      setState("error");
      setMessage("Overrides could not be saved");
    }
  }

  function requestClose() {
    if (dirty) {
      setConfirmDiscard(true);
      return;
    }
    onOpenChange(false);
  }

  return (
    <>
    <Drawer
      open={open}
      onOpenChange={(next) => {
        if (!next) requestClose();
        else onOpenChange(true);
      }}
      title="Metadata"
      description={`What Deluno stores for this ${mediaLabel}`}
      onSubmit={(event) => {
        event.preventDefault();
        void handleSave();
      }}
      footer={
        <DrawerFooter
          state={state}
          message={message}
          saveLabel="Save metadata"
          onCancel={requestClose}
          disabled={busy !== null}
        />
      }
    >
      <DrawerSection title="Provider record" aside={provider ? provider.toUpperCase() : "Not linked"}>
        <div className="flex items-start gap-[var(--grid-gap)]">
          {posterUrl ? (
            <img src={posterUrl} alt="" className="h-28 w-[74px] shrink-0 rounded-[10px] border border-hairline object-cover" />
          ) : null}
          <div className="min-w-0 flex-1">
            <DrawerFacts
              items={[
                { label: "Provider", value: provider?.toUpperCase() ?? "Not linked" },
                { label: "Provider ID", value: providerId ?? "—", mono: true },
                { label: "IMDb", value: value.imdbId || "—", mono: true }
              ]}
            />
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="outline" size="sm" onClick={() => void handleRefresh()} disabled={busy !== null}>
            {busy === "refresh" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Refresh from provider
          </Button>
          {externalUrl ? (
            <Button asChild variant="outline" size="sm">
              <a href={externalUrl} target="_blank" rel="noreferrer">
                <ExternalLink className="h-4 w-4" />
                Open provider page
              </a>
            </Button>
          ) : null}
        </div>
      </DrawerSection>

      <DrawerSection title="Correct the match">
        <Field
          label="Search the provider"
          help={`Pick the right ${mediaLabel} and Deluno refreshes artwork, IDs, genres, ratings and overview from that match.`}
        >
          <div className="flex gap-2">
            <Input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder={title}
              onKeyDown={(event) => {
                if (event.key === "Enter") {
                  event.preventDefault();
                  void handleSearch();
                }
              }}
            />
            <Button type="button" variant="outline" onClick={() => void handleSearch()} disabled={busy !== null}>
              {busy === "search" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Find
            </Button>
          </div>
        </Field>

        {matches.length ? (
          <div className="grid gap-1.5">
            {matches.map((match) => (
              <div
                key={`${match.provider}:${match.providerId}`}
                className="flex items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-hairline px-[var(--field-pad-x)] py-2"
              >
                <div className="min-w-0">
                  <p className="truncate text-[length:var(--type-body-sm)] font-medium text-foreground">
                    {match.title}
                    {match.year ? <span className="ml-1 font-normal text-muted-foreground">{match.year}</span> : null}
                  </p>
                  <p className="mt-0.5 line-clamp-1 text-[length:var(--type-caption)] text-muted-foreground">
                    {match.overview ?? `${match.provider.toUpperCase()} ${match.providerId}`}
                  </p>
                </div>
                <Button type="button" size="sm" onClick={() => void handleLink(match)} disabled={busy !== null}>
                  {busy === `link:${match.providerId}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                  Use this
                </Button>
              </div>
            ))}
          </div>
        ) : searched && busy === null ? (
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            No matches. Try adding the year, the original title, or an IMDb ID.
          </p>
        ) : null}
      </DrawerSection>

      <DrawerSection>
        <Disclosure
          title="Fine-tune"
          summary="Write values by hand when the provider record is wrong. Clearing a field removes the override."
          open={fineTuneOpen}
          onOpenChange={setFineTuneOpen}
        >
          <FieldRow>
            <Field label="Original title" optional>
              <Input value={form.originalTitle} onChange={(event) => edit({ originalTitle: event.target.value })} />
            </Field>
            <Field label="IMDb ID" optional>
              <Input value={form.imdbId} onChange={(event) => edit({ imdbId: event.target.value })} placeholder="tt0000000" />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Poster URL" optional>
              <Input value={form.posterUrl} onChange={(event) => edit({ posterUrl: event.target.value })} />
            </Field>
            <Field label="Backdrop URL" optional>
              <Input value={form.backdropUrl} onChange={(event) => edit({ backdropUrl: event.target.value })} />
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Rating" help="0 to 10." optional>
              <Input value={form.rating} onChange={(event) => edit({ rating: event.target.value })} inputMode="decimal" />
            </Field>
            <Field label="Genres" help="Comma separated." optional>
              <Input value={form.genres} onChange={(event) => edit({ genres: event.target.value })} />
            </Field>
          </FieldRow>
          <Field label="External URL" optional>
            <Input value={form.externalUrl} onChange={(event) => edit({ externalUrl: event.target.value })} />
          </Field>
          <Field label="Overview" optional>
            <Textarea value={form.overview} onChange={(event) => edit({ overview: event.target.value })} rows={4} />
          </Field>
        </Disclosure>
      </DrawerSection>
    </Drawer>

    <ConfirmDialog
      open={confirmDiscard}
      onOpenChange={(next) => {
        if (next) return;
        setConfirmDiscard(false);
      }}
      title="Discard unsaved changes?"
      description="The metadata values you typed haven't been saved."
      confirmLabel="Discard"
      onConfirm={() => {
        setConfirmDiscard(false);
        setState("clean");
        setForm(value);
        onOpenChange(false);
      }}
    />
    </>
  );
}

function validate(form: MediaMetadataValue): string | null {
  if (form.rating.trim()) {
    const rating = Number(form.rating);
    if (!Number.isFinite(rating) || rating < 0 || rating > 10) return "Rating must be a number between 0 and 10";
  }
  for (const [label, url] of [
    ["Poster URL", form.posterUrl],
    ["Backdrop URL", form.backdropUrl],
    ["External URL", form.externalUrl]
  ] as const) {
    if (url.trim() && !isAllowedUrl(url.trim())) return `${label} must be an http or https URL, or a Deluno artwork path`;
  }
  return null;
}

/**
 * Provider artwork is stored as a relative path through Deluno's own proxy
 * (`/api/metadata/artwork/<hash>`), so demanding an absolute http(s) URL refused
 * every save on a title that had metadata — for a field the user never typed in.
 */
function isAllowedUrl(value: string) {
  if (value.startsWith("/")) return !value.startsWith("//");
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}
