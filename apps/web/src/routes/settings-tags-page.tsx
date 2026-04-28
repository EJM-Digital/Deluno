import { useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { LoaderCircle, PencilLine, Trash2 } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { Button } from "../components/ui/button";
import { EmptyState } from "../components/shell/empty-state";
import { emptyPlatformSettingsSnapshot, fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem, type TagItem } from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

interface SettingsTagsLoaderData extends SettingsOverviewLoaderData {
  tags: TagItem[];
}

export async function settingsTagsLoader(): Promise<SettingsTagsLoaderData> {
  const [overview, tags] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<TagItem[]>("/api/tags")
  ]);

  return { ...overview, tags };
}

export function SettingsTagsPage() {
  const loaderData = useLoaderData() as SettingsTagsLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { libraries, tags } = loaderData;
  const revalidator = useRevalidator();
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formState, setFormState] = useState<Record<string, TagItem>>(
    Object.fromEntries(tags.map((tag) => [tag.id, tag]))
  );
  const [createForm, setCreateForm] = useState({ name: "", color: "slate", description: "" });
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusyKey("create");
    setMessage(null);

    try {
      const response = await authedFetch("/api/tags", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(createForm)
      });

      if (!response.ok) {
        throw new Error("Tag could not be created.");
      }

      setCreateForm({ name: "", color: createForm.color, description: "" });
      setMessage("Tag created.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Tag could not be created.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleSave(id: string) {
    const tag = formState[id];
    if (!tag) {
      return;
    }

    setBusyKey(`save:${id}`);
    setMessage(null);

    try {
      const response = await authedFetch(`/api/tags/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: tag.name,
          color: tag.color,
          description: tag.description
        })
      });

      if (!response.ok) {
        throw new Error("Tag could not be updated.");
      }

      setEditingId(null);
      setMessage("Tag updated.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Tag could not be updated.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDelete(id: string) {
    setBusyKey(`delete:${id}`);
    setMessage(null);

    try {
      const response = await authedFetch(`/api/tags/${id}`, { method: "DELETE" });
      if (!response.ok && response.status !== 204) {
        throw new Error("Tag could not be removed.");
      }

      setMessage("Tag removed.");
      revalidator.revalidate();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Tag could not be removed.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <SettingsShell
      title="Tags"
      description="Tags are now a real Deluno platform object. They will become the shared control surface for routing, lists, and future custom-format policy."
    >
      {message ? (
        <div className="rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-sm text-muted-foreground">
          {message}
        </div>
      ) : null}

      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel order-2">
          <CardHeader>
            <CardTitle>Configured tags</CardTitle>
            <CardDescription>Live tags Deluno can now persist and reuse across future policy surfaces.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            {tags.length ? (
              tags.map((tag) => {
                const current = formState[tag.id] ?? tag;
                const editing = editingId === tag.id;

                return (
                  <div key={tag.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                    <div className="flex items-center justify-between gap-3">
                      <div className="min-w-0">
                        {editing ? (
                          <Input
                            value={current.name}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [tag.id]: { ...current, name: event.target.value }
                              }))
                            }
                          />
                        ) : (
                          <div className="flex items-center gap-3">
                            <span className={`inline-flex h-3 w-3 rounded-full ${tagDotClass(current.color)}`} />
                            <p className="font-display text-base font-semibold text-foreground">{current.name}</p>
                          </div>
                        )}
                      </div>

                      <div className="flex items-center gap-2">
                        <Button variant="ghost" size="icon" onClick={() => setEditingId(editing ? null : tag.id)}>
                          <PencilLine className="h-4 w-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => void handleDelete(tag.id)}
                          disabled={busyKey === `delete:${tag.id}`}
                        >
                          {busyKey === `delete:${tag.id}` ? (
                            <LoaderCircle className="h-4 w-4 animate-spin" />
                          ) : (
                            <Trash2 className="h-4 w-4" />
                          )}
                        </Button>
                      </div>
                    </div>

                    <div className="mt-3 grid gap-3 sm:grid-cols-2">
                      <Field label="Color">
                        {editing ? (
                          <ColorSelect
                            value={current.color}
                            onChange={(value) =>
                              setFormState((state) => ({
                                ...state,
                                [tag.id]: { ...current, color: value }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">{current.color}</p>
                        )}
                      </Field>
                      <Field label="Description">
                        {editing ? (
                          <Input
                            value={current.description}
                            onChange={(event) =>
                              setFormState((state) => ({
                                ...state,
                                [tag.id]: { ...current, description: event.target.value }
                              }))
                            }
                          />
                        ) : (
                          <p className="text-sm text-muted-foreground">
                            {current.description || "No description yet"}
                          </p>
                        )}
                      </Field>
                    </div>

                    {editing ? (
                      <div className="mt-4">
                        <Button onClick={() => void handleSave(tag.id)} disabled={busyKey === `save:${tag.id}`}>
                          {busyKey === `save:${tag.id}` ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                          Save tag
                        </Button>
                      </div>
                    ) : null}
                  </div>
                );
              })
            ) : (
              <EmptyState
                size="sm"
                variant="custom"
                title="No tags yet"
                description="Tags let you group libraries, providers, and profiles under shared labels."
              />
            )}
          </CardContent>
        </Card>

        <Card className="settings-panel order-1">
          <CardHeader>
            <CardTitle>Add tag</CardTitle>
            <CardDescription>Start the tag library now; assignment will be layered onto routing, lists, and title operations next.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-[var(--page-gap)]">
            <form className="space-y-3" onSubmit={handleCreate}>
              <Field label="Name">
                <Input
                  value={createForm.name}
                  onChange={(event) => setCreateForm((state) => ({ ...state, name: event.target.value }))}
                />
              </Field>
              <Field label="Color">
                <ColorSelect
                  value={createForm.color}
                  onChange={(value) => setCreateForm((state) => ({ ...state, color: value }))}
                />
              </Field>
              <Field label="Description">
                <Input
                  value={createForm.description}
                  onChange={(event) => setCreateForm((state) => ({ ...state, description: event.target.value }))}
                />
              </Field>
              <Button type="submit" disabled={busyKey === "create"}>
                {busyKey === "create" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
                Add tag
              </Button>
            </form>

            <div className="density-help space-y-3 text-muted-foreground">
              <BacklogRow title="Libraries ready to tag" copy={`${libraries.length} libraries are already live and are the first obvious assignment targets.`} />
              <BacklogRow title="Routing next" copy="Tags can now become real required/excluded routing gates instead of free-text-only concepts." />
              <BacklogRow title="Lists and formats later" copy="This stored tag model gives Deluno a clean shared primitive for future list intake and custom-format targeting." />
            </div>
          </CardContent>
        </Card>
      </div>
    </SettingsShell>
  );
}

function Field({ children, label }: { children: ReactNode; label: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="density-label uppercase tracking-[0.18em] text-muted-foreground">{label}</p>
      <div style={{ marginTop: "var(--field-label-gap)" }}>{children}</div>
    </div>
  );
}

function ColorSelect({
  value,
  onChange
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  const options = ["slate", "emerald", "teal", "blue", "violet", "amber", "rose"];

  return (
    <select
      value={value}
      onChange={(event) => onChange(event.target.value)}
      className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
    >
      {options.map((option) => (
        <option key={option} value={option}>
          {option}
        </option>
      ))}
    </select>
  );
}

function BacklogRow({ title, copy }: { title: string; copy: string }) {
  return (
    <div className="density-field rounded-xl border border-hairline bg-surface-1">
      <p className="font-medium text-foreground">{title}</p>
      <p className="mt-1">{copy}</p>
    </div>
  );
}

function tagDotClass(color: string) {
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
