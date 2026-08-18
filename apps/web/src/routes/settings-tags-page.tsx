/**
 * Tags — list → drawer. Contracts: GET/POST /api/tags, PUT/DELETE /api/tags/{id}.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Plus } from "lucide-react";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { Textarea } from "../components/ui/textarea";
import { toast } from "../components/shell/toaster";
import { librarySetupNavItems } from "../components/app/settings-shell";
import { fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem, type TagItem } from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { cn } from "../lib/utils";

const COLORS = ["slate", "emerald", "teal", "blue", "violet", "amber", "rose"] as const;

interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
  tags: TagItem[];
}

interface TagForm {
  name: string;
  color: string;
  description: string;
}

type DrawerMode = { kind: "closed" } | { kind: "create" } | { kind: "edit"; id: string };

export async function settingsTagsLoader(): Promise<LoaderData> {
  const [overview, tags] = await Promise.all([settingsOverviewLoader(), fetchJson<TagItem[]>("/api/tags")]);
  return { ...overview, tags };
}

export function SettingsTagsPage() {
  const { tags } = useLoaderData() as LoaderData;
  const revalidator = useRevalidator();
  const sorted = useMemo(() => [...tags].sort((a, b) => a.name.localeCompare(b.name)), [tags]);

  const [mode, setMode] = useState<DrawerMode>({ kind: "closed" });
  const [form, setForm] = useState<TagForm>(emptyForm);
  const [initialForm, setInitialForm] = useState<TagForm>(emptyForm);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [nameError, setNameError] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);

  const isOpen = mode.kind !== "closed";
  const editing = mode.kind === "edit" ? tags.find((tag) => tag.id === mode.id) ?? null : null;
  const dirty = isOpen && (form.name !== initialForm.name || form.color !== initialForm.color || form.description !== initialForm.description);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  const blocker = useUnsavedChanges(dirty);
  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
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
      if (!response.ok) throw new Error(mode.kind === "edit" ? "Tag could not be saved." : "Tag could not be created.");
      if (mode.kind === "create") {
        const created = (await response.json()) as TagItem;
        setMode({ kind: "edit", id: created.id });
        setSaveMessage("Tag created");
      } else {
        setSaveMessage("Saved just now");
      }
      setForm(payload);
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
      if (!response.ok && response.status !== 204) throw new Error("Tag could not be removed.");
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

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        tabs={librarySetupNavItems}
        actions={
          <Button type="button" onClick={() => open(null)}>
            <Plus className="h-4 w-4" />
            New tag
          </Button>
        }
      />

      <ListCard title="Tags" count={tags.length ? `${tags.length} ${tags.length === 1 ? "tag" : "tags"}` : undefined}>
        {tags.length === 0 ? (
          <ListEmpty
            title="No tags yet"
            description="Labels shared by libraries, final destinations and routing — Kids, 4K, Anime, or a processor workflow. Add only the ones that make you manage media differently."
            actions={
              <Button type="button" size="sm" onClick={() => open(null)}>
                <Plus className="h-3.5 w-3.5" />
                New tag
              </Button>
            }
          />
        ) : (
          <ListTable columns={[{ label: "Name" }, { label: "Colour" }, { label: "Description", width: "minmax(0,2fr)" }]}>
            {sorted.map((tag) => (
              <ListRow key={tag.id} onClick={() => open(tag)} selected={mode.kind === "edit" && mode.id === tag.id}>
                <div role="cell" className="flex min-w-0 items-center gap-2.5">
                  <span aria-hidden className={cn("h-2.5 w-2.5 shrink-0 rounded-full", dotClass(tag.color))} />
                  <span className="truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{tag.name}</span>
                </div>
                <ListCell primary={capitalise(tag.color)} />
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
        description={mode.kind === "create" ? "A label shared by libraries, destinations and routing." : "Tag"}
        onSubmit={handleSubmit}
        footer={<DrawerFooter state={footerState} message={saveMessage} saveLabel={mode.kind === "create" ? "Create tag" : "Save tag"} onCancel={requestClose} disabled={busy} />}
      >
        <DrawerSection title="Basics">
          <FieldRow>
            <Field label="Name" error={nameError} help="Use the same spelling wherever a library or rule refers to it.">
              <Input value={form.name} onChange={(event) => { setNameError(null); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="Kids" autoComplete="off" />
            </Field>
            <Field label="Colour">
              <Select value={form.color} onChange={(event) => setForm((current) => ({ ...current, color: event.target.value }))} options={COLORS.map((color) => ({ value: color, label: capitalise(color) }))} />
            </Field>
          </FieldRow>
          <Field label="Description" optional>
            <Textarea value={form.description} onChange={(event) => setForm((current) => ({ ...current, description: event.target.value }))} placeholder="What this tag is for." rows={2} />
          </Field>
        </DrawerSection>
        {editing ? (
          <DrawerSection>
            <DrawerDanger title="Delete this tag" description="Anything referring to it simply loses the label." action={<Button type="button" variant="destructive" size="sm" onClick={() => setConfirmRemove(true)} disabled={busy}>Delete</Button>} />
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog open={confirmRemove} onOpenChange={setConfirmRemove} title={`Delete “${editing?.name ?? form.name}”?`} description="Anything referring to this tag simply loses the label." confirmLabel="Delete tag" busy={busy} onConfirm={() => void handleRemove()} />
      <ConfirmDialog
        open={confirmDiscard || blocker.state === "blocked"}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
          if (blocker.state === "blocked") blocker.reset();
        }}
        title="Discard unsaved changes?"
        description="Your edits to this tag haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          if (blocker.state === "blocked") {
            setMode({ kind: "closed" });
            blocker.proceed();
          } else {
            closeDrawer();
          }
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
