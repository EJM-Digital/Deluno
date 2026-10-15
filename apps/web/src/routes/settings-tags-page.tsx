/**
 * Tags — list → drawer. Contracts: GET/POST /api/tags, PUT/DELETE /api/tags/{id}.
 */
import { settleFormAfterSave } from "../lib/settle-form-after-save";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { describeTagInUse, describeTagUsage, tagUserKindLabel, unusedTags } from "../lib/tag-users";
import { Plus } from "lucide-react";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { Textarea } from "../components/ui/textarea";
import { toast } from "../components/shell/toaster";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { readErrorText, fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem, type TagItem, type TagUsageItem } from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

const COLORS = ["slate", "emerald", "teal", "blue", "violet", "amber", "rose"] as const;

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
  tags: TagItem[];
  usage: TagUsageItem[];
}

interface TagForm {
  name: string;
  color: string;
  description: string;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsTagsLoader(): Promise<LoaderData> {
  const [overview, tags, usage] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<TagItem[]>("/api/tags"),
    fetchJson<TagUsageItem[]>("/api/tags/usage").catch(() => [])
  ]);
  return { ...overview, tags, usage };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { areas: [ConfigAreas.tags] };

export function SettingsTagsPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { tags, usage } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const usageById = useMemo(() => new Map(usage.map((item) => [item.id, item])), [usage]);
  const sorted = useMemo(() => [...tags].sort((a, b) => a.name.localeCompare(b.name)), [tags]);

  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<TagForm>(emptyForm);
  const [initialForm, setInitialForm] = useState<TagForm>(emptyForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [nameError, setNameError] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmRemoveUnused, setConfirmRemoveUnused] = useState(false);
  const unused = useMemo(() => unusedTags(tags, usage), [tags, usage]);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? tags.find((tag) => tag.id === mode.id) ?? null : null;
  const dirty = isOpen && (form.name !== initialForm.name || form.color !== initialForm.color || form.description !== initialForm.description);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  function open(tag: TagItem | null) {
    const next = tag ? { name: tag.name, color: tag.color, description: tag.description ?? "" } : emptyForm();
    setMode(tag ? { kind: "edit", id: tag.id } : { kind: "create" });
    setForm(next);
    setInitialForm(next);
    setSaveState(undefined);
    setNameError(null);
  }
  function closeDrawer() {
    setMode({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!isOpen || busy) return;
    if (!form.name.trim()) {
      setNameError("Give the tag a name.");
      return;
    }
    setBusy(true);
    setSaveState("saving");
    try {
      const payload = { name: form.name.trim(), color: form.color, description: form.description.trim() };
      const response = await authedFetch(mode.kind === "edit" ? `/api/tags/${mode.id}` : "/api/tags", { method: mode.kind === "edit" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      if (!response.ok) throw new Error(await readErrorText(response, mode.kind === "edit" ? "Tag could not be saved." : "Tag could not be created."));
      if (mode.kind === "create") {
        const created = (await response.json()) as TagItem;
        setMode({ kind: "edit", id: created.id });
        setSaveMessage("Tag created");
      } else {
        setSaveMessage("Saved just now");
      }
      setForm((current) => settleFormAfterSave(current, form, payload));
      setInitialForm(payload);
      setSaveState("saved");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(false);
    }
  }

  async function handleRemove() {
    if (mode.kind !== "edit") return;
    setBusy(true);
    try {
      const response = await authedFetch(`/api/tags/${mode.id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) throw new Error(await readErrorText(response, "Tag could not be removed."));
      toast.success(`${editing?.name ?? "Tag"} removed`);
      setConfirmRemove(false);
      setInitialForm(form);
      closeDrawer();
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Tag could not be removed.");
    } finally {
      setBusy(false);
    }
  }

  async function handleRemoveUnused() {
    setBusy(true);
    try {
      const response = await authedFetch("/api/tags/delete-unused", { method: "POST" });
      if (!response.ok) throw new Error(await readErrorText(response, "Unused tags could not be deleted."));
      toast.success(`Deleted ${unused.length} unused tag${unused.length === 1 ? "" : "s"}`);
      setConfirmRemoveUnused(false);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unused tags could not be deleted.");
    } finally {
      setBusy(false);
    }
  }

  const editingUsage = editing ? usageById.get(editing.id) : undefined;

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        tabs={librarySetupNavItems}
        actions={
          <>
            {unused.length > 0 ? (
              <Button type="button" size="sm" variant="outline" onClick={() => setConfirmRemoveUnused(true)}>
                Delete unused tags ({unused.length})
              </Button>
            ) : null}
            <PageToolbarAction onClick={() => open(null)}>New tag</PageToolbarAction>
          </>
        }
      />

      <ListCard title={tags.length ? `${tags.length} ${tags.length === 1 ? "tag" : "tags"}` : "0 tags"} titleCase={false}>
        {tags.length === 0 ? (
          <ListEmpty
            title="No tags yet"
            description="Making one takes a name and a colour. What it does depends on where you apply it afterwards."
            actions={
              <Button type="button" size="sm" onClick={() => open(null)}>
                <Plus className="h-3.5 w-3.5" />
                New tag
              </Button>
            }
          />
        ) : (
        <ListTable
            listId="tags"
            items={sorted}
            columns={[
              { id: "name", label: "Name", sort: (tag) => tag.name.toLowerCase() },
              { id: "colour", label: "Colour", sort: (tag) => tag.color },
              { id: "usedBy", label: "Used by", sort: (tag) => describeTagUsage(usageById.get(tag.id)).toLowerCase() },
              { id: "description", label: "Description", width: "minmax(0,2fr)", sort: (tag) => (tag.description ?? "").toLowerCase() }
            ]}
          >
            {(rows) => rows.map((tag) => (
              <ListRow key={tag.id} onClick={() => open(tag)} selected={mode.kind === "edit" && mode.id === tag.id}>
                <div role="cell" className="flex min-w-0 items-center gap-2.5">
                  <span aria-hidden className={cn("h-2.5 w-2.5 shrink-0 rounded-full", dotClass(tag.color))} />
                  <span className="truncate text-(length:--type-body-sm) font-semibold text-foreground">{tag.name}</span>
                </div>
                <ListCell primary={capitalise(tag.color)} />
                <ListCell primary={describeTagUsage(usageById.get(tag.id))} />
                <ListCell primary={tag.description || <span className="text-muted-foreground">—</span>} />
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={isOpen}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={mode.kind === "create" ? "New tag" : editing?.name ?? form.name}
        description={mode.kind === "create" ? "Create a label first, then apply it where you need it." : "Reusable label"}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create tag" : "Save tag"} onCancel={requestClose} saveEnabled={mode.kind === "create" ? true : undefined} disabled={busy} />}
      >
        <DrawerSection title="Tag details">
          <FieldRow>
            <Field label="Name" error={nameError} help="Renaming changes the tag everywhere it is used. Tags are case-insensitive.">
              <Input value={form.name} onChange={(event) => { setNameError(null); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Name this tag, for example Kids" autoComplete="off" />
            </Field>
            <Field label="Colour">
              <Select value={form.color} onChange={(event) => setForm((current) => ({ ...current, color: event.target.value }))} options={COLORS.map((color) => ({ value: color, label: capitalise(color) }))} />
            </Field>
          </FieldRow>
          <Field label="What this tag means" optional help="This note is for you; it does not change matching or routing.">
            <Textarea value={form.description} onChange={(event) => setForm((current) => ({ ...current, description: event.target.value }))} placeholder="For example: titles sent to the lounge TV." rows={2} />
          </Field>
        </DrawerSection>
        {editing ? (
          <DrawerSection title="Where this tag is used" aside="Renaming the tag changes it in all of these.">
            {editingUsage?.users.length ? (
              <ul className="grid gap-1.5" aria-label="Settings that use this tag">
                {editingUsage.users.map((user) => (
                  <li key={`${user.kind}-${user.id}`} className="flex items-center justify-between gap-3 text-(length:--type-body-sm)">
                    <Link to={user.link} className="truncate font-medium text-foreground underline-offset-2 hover:underline">{user.name}</Link>
                    <span className="shrink-0 text-muted-foreground">{tagUserKindLabel(user.kind)}</span>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-(length:--type-body-sm) text-muted-foreground">No indexer, download client, notification, library or rule uses this tag.</p>
            )}
            {editingUsage?.totalCount ? (
              <p className="text-(length:--type-body-sm) text-muted-foreground">{describeTagUsage({ ...editingUsage, users: [] })} carry this tag.</p>
            ) : null}
          </DrawerSection>
        ) : null}
        {editing ? (
          <DrawerSection>
            {editingUsage && !editingUsage.isUnused ? (
              <p className="text-(length:--type-body-sm) text-muted-foreground">
                In use by {describeTagInUse(editingUsage)} — remove it from those first, then it can be deleted.
              </p>
            ) : (
              <DrawerDanger title="Delete this tag" description="Nothing uses this tag." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
            )}
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Delete “${editing?.name ?? form.name}”?`}
        description="Nothing uses this tag. This cannot be undone."
        confirmLabel="Delete tag"
        busy={busy}
        onConfirm={() => void handleRemove()}
      />

      <ConfirmDialog
        open={confirmRemoveUnused}
        onOpenChange={setConfirmRemoveUnused}
        title={`Delete ${unused.length} unused tag${unused.length === 1 ? "" : "s"}?`}
        description="These tags are not on any title, connection or rule. This cannot be undone."
        confirmLabel="Delete unused tags"
        busy={busy}
        onConfirm={() => void handleRemoveUnused()}
        extraContent={<p className="text-(length:--type-body-sm)">{unused.map((tag) => tag.name).join(", ")}</p>}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits to this tag haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

function emptyForm(): TagForm {
  return { name: "", color: "slate", description: "" };
}
function capitalise(value: string) {
  return value.charAt(0).toUpperCase() + value.slice(1);
}
function dotClass(color: string) {
  switch (color) {
    case "emerald":
      return "bg-emerald-500";
    case "teal":
      return "bg-teal-500";
    case "blue":
      return "bg-sky-500";
    case "violet":
      return "bg-violet-500";
    case "amber":
      return "bg-amber-500";
    case "rose":
      return "bg-rose-500";
    default:
      return "bg-slate-400";
  }
}
