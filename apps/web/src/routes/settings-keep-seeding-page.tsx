/**
 * Settings -> Keep Seeding (#1240).
 *
 * When a strike rule reaches its count on a torrent from a private tracker, deleting it can cost a ratio or earn a
 * hit-and-run. A rule set to "move it" puts the torrent in a category the person chose in their own download client
 * instead, so it keeps seeding, and Deluno stops tracking it and looks for another copy. Nothing is deleted.
 *
 * This page is where the person says which sites are private and which category a moved torrent goes to: one for a
 * download client, one for a site (which beats the client's) and one for a tag (which beats both). Nothing is set
 * on a new install, and no rule moves anything until one is told to on Download Health.
 */
import { useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { PageLink } from "../components/app/page-link";
import { automationNavItems } from "../components/app/settings-shell";
import { toast } from "../components/shell/toaster";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { ConfigAreas } from "../lib/config-areas";
import { fetchJson } from "../lib/api";
import type {
  KeepSeedingCategoryCheck,
  KeepSeedingClient,
  KeepSeedingIndexer,
  KeepSeedingProblem,
  KeepSeedingView
} from "../lib/api";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { authedFetch } from "../lib/use-auth";

export async function settingsKeepSeedingLoader(): Promise<KeepSeedingView> {
  return fetchJson<KeepSeedingView>("/api/download-health/keep-seeding");
}

/** What makes this page read again: a rule or a choice changed anywhere, or a move that did not work appeared or ended. */
const REFRESH: LiveRefreshSpec = { events: ["DownloadHealthChanged"], areas: [ConfigAreas.downloadHealth] };

type Drawing = { kind: "closed" } | { kind: "client"; id: string } | { kind: "site"; id: string };

const PRIVATE_OPTIONS = (known: string) => [
  { value: "follow", label: `Follow what Deluno knows (${privacyWords(known)})` },
  { value: "yes", label: "Yes, treat it as private" },
  { value: "no", label: "No, treat it as public" }
];

function privacyWords(privacy: string) {
  switch (privacy) {
    case "private": return "private";
    case "semi-private": return "semi-private";
    case "public": return "public";
    default: return "not set";
  }
}

function overrideToValue(value: boolean | null) {
  return value === null ? "follow" : value ? "yes" : "no";
}

function valueToOverride(value: string): boolean | null {
  return value === "yes" ? true : value === "no" ? false : null;
}

function splitTags(text: string) {
  return text.split(",").map((part) => part.trim()).filter(Boolean);
}

/** The whole choice set, as the server stores it, built from what the screen shows. */
function settingsBody(view: KeepSeedingView) {
  return {
    clientCategories: Object.fromEntries(view.clients.filter((client) => client.category).map((client) => [client.clientId, client.category])),
    indexers: Object.fromEntries(
      view.indexers
        .filter((site) => site.privateOverride !== null || site.tags.length > 0 || site.category)
        .map((site) => [site.indexerId, { privateOverride: site.privateOverride, tags: site.tags, category: site.category }])
    ),
    tagCategories: Object.fromEntries(view.tagCategories.map((item) => [item.tag, item.category]))
  };
}

async function readProblem(response: Response, fallback: string) {
  const problem = await response.json().catch(() => null) as { errors?: Record<string, string[]> } | null;
  return (problem?.errors ? Object.values(problem.errors).flat()[0] : null) ?? fallback;
}

export function SettingsKeepSeedingPage() {
  useLiveRefresh(REFRESH);
  const loaded = useLoaderData() as KeepSeedingView;
  const revalidator = useRevalidator();
  // What this page last saved wins over the loader until the loader reads again.
  const [saved, setSaved] = useState<{ from: KeepSeedingView; view: KeepSeedingView } | null>(null);
  const view = saved && saved.from === loaded ? saved.view : loaded;
  const [drawing, setDrawing] = useState<Drawing>({ kind: "closed" });
  const [category, setCategory] = useState("");
  const [initialCategory, setInitialCategory] = useState("");
  const [privateChoice, setPrivateChoice] = useState("follow");
  const [initialPrivateChoice, setInitialPrivateChoice] = useState("follow");
  const [tagText, setTagText] = useState("");
  const [initialTagText, setInitialTagText] = useState("");
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [check, setCheck] = useState<KeepSeedingCategoryCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [tagDraft, setTagDraft] = useState({ tag: "", category: "" });
  const [busy, setBusy] = useState<string | null>(null);

  const client = drawing.kind === "client" ? view.clients.find((item) => item.clientId === drawing.id) ?? null : null;
  const site = drawing.kind === "site" ? view.indexers.find((item) => item.indexerId === drawing.id) ?? null : null;
  const open = drawing.kind !== "closed";
  const dirty = open && (category !== initialCategory || privateChoice !== initialPrivateChoice || tagText !== initialTagText);
  useUnsavedChanges(dirty);

  function closeDrawer() {
    setDrawing({ kind: "closed" });
    setCheck(null);
    setSaveState(undefined);
    setSaveMessage(null);
  }

  function openClient(item: KeepSeedingClient) {
    if (!item.canKeepSeeding) return;
    setDrawing({ kind: "client", id: item.clientId });
    setCategory(item.category ?? "");
    setInitialCategory(item.category ?? "");
    setPrivateChoice("follow");
    setInitialPrivateChoice("follow");
    setTagText("");
    setInitialTagText("");
    setCheck(null);
    setSaveState(undefined);
    setSaveMessage(null);
    // A category already chosen is checked as the drawer opens, so a Label plugin turned off, a Transmission too old
    // for labels or a category deleted in the client is said on screen without a click.
    if (item.category) void runCheck(item.clientId, item.category);
  }

  function openSite(item: KeepSeedingIndexer) {
    setDrawing({ kind: "site", id: item.indexerId });
    setCategory(item.category ?? "");
    setInitialCategory(item.category ?? "");
    setPrivateChoice(overrideToValue(item.privateOverride));
    setInitialPrivateChoice(overrideToValue(item.privateOverride));
    setTagText(item.tags.join(", "));
    setInitialTagText(item.tags.join(", "));
    setCheck(null);
    setSaveState(undefined);
    setSaveMessage(null);
  }

  /** Saves the whole set and reads back what the server kept. Throws the server's own reason when it refuses. */
  async function save(next: KeepSeedingView) {
    const response = await authedFetch("/api/download-health/keep-seeding", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(settingsBody(next))
    });
    if (!response.ok) throw new Error(await readProblem(response, "That could not be saved."));
    const kept = await response.json() as KeepSeedingView;
    setSaved({ from: loaded, view: kept });
    revalidator.revalidate();
    return kept;
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saveState === "saving") return;
    setSaveState("saving");
    setSaveMessage(null);
    try {
      const trimmed = category.trim();
      if (drawing.kind === "client" && client) {
        await save({ ...view, clients: view.clients.map((item) => item.clientId === client.clientId ? { ...item, category: trimmed || null } : item) });
      } else if (drawing.kind === "site" && site) {
        await save({
          ...view,
          indexers: view.indexers.map((item) => item.indexerId === site.indexerId
            ? { ...item, privateOverride: valueToOverride(privateChoice), tags: splitTags(tagText), category: trimmed || null }
            : item)
        });
      }
      setSaveState("saved");
      setSaveMessage("Saved just now");
      closeDrawer();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "That could not be saved.");
    }
  }

  async function runCheck(clientId: string, typed?: string) {
    const name = (typed ?? category).trim();
    if (!name) {
      setCheck(null);
      setSaveMessage("Enter a category first.");
      setSaveState("error");
      return;
    }
    setChecking(true);
    try {
      const response = await authedFetch("/api/download-health/keep-seeding/check", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ clientId, category: name })
      });
      if (!response.ok) throw new Error(await readProblem(response, "Deluno could not check that."));
      setCheck(await response.json() as KeepSeedingCategoryCheck);
      setSaveState(undefined);
    } catch (error) {
      setCheck(null);
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Deluno could not check that.");
    } finally {
      setChecking(false);
    }
  }

  async function addTag(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const tag = tagDraft.tag.trim();
    const categoryName = tagDraft.category.trim();
    if (!tag || !categoryName) {
      toast.error("Give the tag a name and the category its torrents move to.");
      return;
    }
    setBusy("tag");
    try {
      await save({ ...view, tagCategories: [...view.tagCategories.filter((item) => item.tag.toLowerCase() !== tag.toLowerCase()), { tag, category: categoryName }] });
      setTagDraft({ tag: "", category: "" });
      toast.success(`Torrents tagged ${tag} will move to ${categoryName}`);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That could not be saved.");
    } finally {
      setBusy(null);
    }
  }

  async function removeTag(tag: string) {
    setBusy(`tag:${tag}`);
    try {
      await save({ ...view, tagCategories: view.tagCategories.filter((item) => item.tag !== tag) });
      toast.success(`${tag} no longer picks a category`);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That could not be saved.");
    } finally {
      setBusy(null);
    }
  }

  async function clearProblem(problem: KeepSeedingProblem) {
    setBusy(`problem:${problem.key}`);
    try {
      const response = await authedFetch(`/api/download-health/keep-seeding/problems/${encodeURIComponent(problem.key)}/clear`, { method: "POST" });
      if (!response.ok && response.status !== 404) throw new Error("That could not be cleared.");
      toast.success(`${problem.releaseName} cleared`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That could not be cleared.");
    } finally {
      setBusy(null);
    }
  }

  const torrentClients = view.clients.filter((item) => item.canKeepSeeding);
  const hasAnythingSet = view.clients.some((item) => item.category) || view.indexers.some((item) => item.category || item.tags.length > 0 || item.privateOverride !== null) || view.tagCategories.length > 0;
  const drawerTitle = client ? client.name : site ? site.name : "";
  const footerState: DrawerSaveState = saveState ?? (dirty ? "dirty" : "clean");

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar tabs={automationNavItems} />

      <p className="text-(length:--type-body-sm) text-muted-foreground">
        A private tracker can ban an account for deleting a torrent early. When a rule on <PageLink page="downloadHealth" /> is set to move it, a torrent from a private tracker that gets struck goes to the category you choose in your download client, keeps running there, and Deluno stops tracking it and looks for another copy. Deluno never deletes its files. Nothing here is on until a rule is told to.
      </p>

      {view.problems.length > 0 ? (
        <ListCard title="Moves that did not work" count={`${view.problems.length} waiting`}>
          <ListTable
            listId="keep-seeding-problems"
            items={view.problems}
            columns={[
              { id: "release", label: "Download", width: "minmax(0,1.2fr)", sort: (problem) => problem.releaseName.toLowerCase() },
              { id: "reason", label: "What happened", width: "minmax(0,2fr)", sort: (problem) => problem.message.toLowerCase() },
              { id: "actions", label: "", width: "100px" }
            ]}
          >
            {(rows) => rows.map((problem) => (
              <ListRow key={problem.key}>
                <ListNameCell name={problem.releaseName} sub={problem.clientName} />
                <ListCell
                  primary={problem.message}
                  secondary={problem.kind === "refused" ? `Tried ${problem.attempts} ${problem.attempts === 1 ? "time" : "times"}. Deluno tries again at every check and has deleted nothing.` : "Left alone until a category is set."}
                />
                <ListCell primary={
                  <Button type="button" variant="outline" size="sm" aria-label={`Clear ${problem.releaseName}`} onClick={() => void clearProblem(problem)} disabled={busy === `problem:${problem.key}`}>
                    Clear
                  </Button>
                } />
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      <ListCard title="Download clients" count="A category for every torrent moved off this client">
        {view.clients.length === 0 ? (
          <ListEmpty title="No download clients" description="Add a download client first. A moved torrent stays in it, in the category you choose." />
        ) : (
          <ListTable
            listId="keep-seeding-clients"
            items={view.clients}
            columns={[
              { id: "client", label: "Client", width: "minmax(0,1.2fr)", sort: (item) => item.name.toLowerCase() },
              { id: "category", label: "Keep-seeding category", width: "minmax(0,1.5fr)", sort: (item) => (item.category ?? "").toLowerCase() }
            ]}
          >
            {(rows) => rows.map((item) => (
              <ListRow key={item.clientId} onClick={item.canKeepSeeding ? () => openClient(item) : undefined} selected={drawing.kind === "client" && drawing.id === item.clientId}>
                <ListNameCell name={item.name} sub={item.protocol} />
                <ListCell
                  primary={item.canKeepSeeding ? (item.category ?? "Not set") : "Not offered"}
                  secondary={item.canKeepSeeding ? (item.category ? undefined : "A private torrent has nowhere to go from here unless its tag or site names a category") : item.reason ?? undefined}
                />
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Torrent sites" count="Which are private, and where their torrents go instead of being deleted">
        {view.indexers.length === 0 ? (
          <ListEmpty title="No torrent sites" description="Add a torrent site under Connections. Sites added from a tracker definition know whether they are private." />
        ) : (
          <ListTable
            listId="keep-seeding-sites"
            items={view.indexers}
            columns={[
              { id: "site", label: "Site", width: "minmax(0,1.2fr)", sort: (item) => item.name.toLowerCase() },
              { id: "private", label: "Private", width: "minmax(0,1fr)", sort: (item) => (item.isPrivate ? 0 : 1) },
              { id: "tags", label: "Tags", width: "minmax(0,1fr)", sort: (item) => item.tags.join(",").toLowerCase() },
              { id: "category", label: "Keep-seeding category", width: "minmax(0,1.2fr)", sort: (item) => (item.category ?? "").toLowerCase() }
            ]}
          >
            {(rows) => rows.map((item) => (
              <ListRow key={item.indexerId} onClick={() => openSite(item)} selected={drawing.kind === "site" && drawing.id === item.indexerId}>
                <ListNameCell name={item.name} sub={item.privateOverride === null ? `Deluno knows: ${privacyWords(item.privacy)}` : "You set this"} />
                <ListCell primary={<Chip tone={item.isPrivate ? "warn" : "idle"}>{item.isPrivate ? "Private" : "Not private"}</Chip>} />
                <ListCell primary={item.tags.length ? item.tags.join(", ") : "None"} />
                <ListCell primary={item.category ?? "Uses the client's"} />
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <ListCard title="Tags" count="A tag's category beats the site's, which beats the client's">
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            Give sites a tag above, then say here where a torrent from any site with that tag goes. These tags only choose a category: they never change what a site is searched for. A site with a tag keeps its torrents seeding even if it is not marked private.
          </p>
          <form onSubmit={(event) => void addTag(event)} className="grid gap-(--grid-gap) sm:grid-cols-[1fr_1fr_auto]">
            <Input aria-label="Tag name" placeholder="Tag, for example ratio-sensitive" value={tagDraft.tag} onChange={(event) => setTagDraft((current) => ({ ...current, tag: event.target.value }))} />
            <Input aria-label="Category for this tag" placeholder="Category in your download client" value={tagDraft.category} onChange={(event) => setTagDraft((current) => ({ ...current, category: event.target.value }))} />
            <Button type="submit" size="sm" disabled={busy === "tag"}>Add tag</Button>
          </form>
          {view.tagCategories.length === 0 ? null : (
            <ListTable
              listId="keep-seeding-tags"
              items={view.tagCategories}
              columns={[
                { id: "tag", label: "Tag", sort: (item) => item.tag.toLowerCase() },
                { id: "category", label: "Category", sort: (item) => item.category.toLowerCase() },
                { id: "actions", label: "", width: "100px" }
              ]}
            >
              {(rows) => rows.map((item) => (
                <ListRow key={item.tag}>
                  <ListCell primary={item.tag} />
                  <ListCell primary={item.category} />
                  <ListCell primary={
                    <Button type="button" variant="outline" size="sm" aria-label={`Remove tag ${item.tag}`} onClick={() => void removeTag(item.tag)} disabled={busy === `tag:${item.tag}`}>
                      Remove
                    </Button>
                  } />
                </ListRow>
              ))}
            </ListTable>
          )}
        </div>
      </ListCard>

      {!hasAnythingSet && torrentClients.length > 0 ? (
        <p className="text-(length:--type-body-sm) text-muted-foreground">
          Nothing is set yet. Create a category in your download client, then choose it on a client or a site above.
        </p>
      ) : null}

      <Drawer
        open={open}
        onOpenChange={(nextOpen) => {
          if (!nextOpen) closeDrawer();
        }}
        title={drawerTitle}
        description={client ? "Where torrents moved off this client go" : "Whether this site is private, and where its torrents go"}
        onSubmit={submit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel="Save" onCancel={closeDrawer} />}
      >
        {client ? (
          <DrawerSection title="Keep-seeding category">
            <Field
              label="Category"
              help={`A struck private torrent is moved to this category in ${client.name} so it keeps seeding instead of being deleted. Create the category in ${client.name} first; Deluno never creates it.`}
            >
              <Input value={category} onChange={(event) => { setCategory(event.target.value); setCheck(null); }} placeholder="For example keep-seeding" />
            </Field>
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => void runCheck(client.clientId)} disabled={checking}>
                {checking ? "Checking..." : "Check it"}
              </Button>
              {check ? (
                <>
                  <Chip tone={check.status === "ready" ? "ok" : check.status === "missing" || check.status === "unreachable" ? "bad" : "warn"}>
                    {check.status === "ready" ? "Ready" : check.status === "missing" ? "Not in the client" : check.status === "unreachable" ? "Could not check" : check.status === "unsupported" ? "Not available" : "Needs a name"}
                  </Chip>
                  <span className="text-(length:--type-caption) text-muted-foreground">{check.message}</span>
                </>
              ) : null}
            </div>
          </DrawerSection>
        ) : null}
        {site ? (
          <>
            <DrawerSection title="Is it private?">
              <Field
                label="Treat this site as"
                help="Deluno reads this from the site's tracker definition where it can. Say it yourself here if it is wrong, or if the site was added by hand and nothing says. Either way, a torrent the client reports as private is still kept."
              >
                <Select value={privateChoice} onChange={(event) => setPrivateChoice(event.target.value)} options={PRIVATE_OPTIONS(site.privacy)} />
              </Field>
            </DrawerSection>
            <DrawerSection title="Where its torrents go">
              <Field label="Tags" help="Names you group sites under, separated by commas. A tag's category beats this site's. They never change what the site is searched for.">
                <Input value={tagText} onChange={(event) => setTagText(event.target.value)} placeholder="For example ratio-sensitive" />
              </Field>
              <Field label="Category" help="Leave blank to use the download client's. A tag's category beats this one. Create the category in the download client first.">
                <Input value={category} onChange={(event) => setCategory(event.target.value)} placeholder="Uses the client's" />
              </Field>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>
    </div>
  );
}
