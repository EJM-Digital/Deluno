import { useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { GripVertical, HelpCircle, LoaderCircle, Plus, Sparkles, Trash2, X } from "lucide-react";
import {
  DndContext,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent
} from "@dnd-kit/core";
import {
  arrayMove,
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { restrictToVerticalAxis, restrictToParentElement } from "@dnd-kit/modifiers";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { toast } from "../components/shell/toaster";
import { QualityProfileWizard, type ProfileDraft } from "../components/app/quality-profile-wizard";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type CustomFormatItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { cn } from "../lib/utils";
import { authedFetch } from "../lib/use-auth";
import { findBundledCF } from "../lib/trash-guide-data";
import { RouteSkeleton } from "../components/shell/skeleton";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  settings: PlatformSettingsSnapshot;
}

export async function settingsProfilesLoader(): Promise<SettingsOverviewLoaderData> {
  const [overview, customFormats] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats")
  ]);
  return { ...overview, customFormats };
}

/* ── Helper: media type badge ────────────────────────────────────── */
const MEDIA_BADGES: Record<string, { label: string; cls: string }> = {
  movies: { label: "Movies", cls: "bg-sky-500/10 text-sky-400 border-sky-500/20" },
  tv:     { label: "TV",     cls: "bg-violet-500/10 text-violet-400 border-violet-500/20" },
  anime:  { label: "Anime",  cls: "bg-pink-500/10 text-pink-400 border-pink-500/20" },
};

/* ── Inline help tooltip ─────────────────────────────────────────── */
function HelpTip({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="relative inline-block">
      <button
        type="button"
        onMouseEnter={() => setOpen(true)}
        onMouseLeave={() => setOpen(false)}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
        className="text-muted-foreground/50 hover:text-muted-foreground"
      >
        <HelpCircle className="h-3.5 w-3.5" />
      </button>
      {open && (
        <div className="absolute bottom-full left-1/2 z-50 mb-2 w-56 -translate-x-1/2 rounded-xl border border-hairline bg-popover px-3 py-2 text-[11.5px] leading-relaxed text-muted-foreground shadow-xl">
          {text}
        </div>
      )}
    </div>
  );
}

/* ── Sortable profile row ─────────────────────────────────────────── */
function SortableProfileRow({
  id,
  profile,
  customFormats,
  busyKey,
  onDelete
}: {
  id: string;
  profile: QualityProfileItem;
  customFormats: CustomFormatItem[];
  busyKey: string | null;
  onDelete: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id });
  const badge = MEDIA_BADGES[profile.mediaType ?? "movies"] ?? MEDIA_BADGES.movies;

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.4 : 1,
  };

  const allowedCount = (profile.allowedQualities ?? "").split(",").filter(Boolean).length;
  const cfIds = (profile.customFormatIds ?? "").split(",").map((value) => value.trim()).filter(Boolean);
  const cfCount = cfIds.length;
  const cfNames = cfIds
    .map((idValue) => customFormats.find((format) => format.id === idValue || format.trashId === idValue)?.name)
    .filter((name): name is string => Boolean(name));

  return (
    <div
      ref={setNodeRef}
      style={style}
      className={cn(
        "group flex items-start gap-3 rounded-2xl border border-hairline bg-surface-1 p-4 transition-shadow",
        isDragging && "shadow-2xl ring-2 ring-primary/20"
      )}
    >
      {/* Drag handle */}
      <button
        type="button"
        {...attributes}
        {...listeners}
        aria-label="Drag to reorder"
        className="mt-0.5 cursor-grab touch-none rounded-lg p-1.5 text-muted-foreground/30 hover:bg-muted/30 hover:text-muted-foreground active:cursor-grabbing"
      >
        <GripVertical className="h-4 w-4" />
      </button>

      {/* Content */}
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <p className="font-semibold text-foreground">{profile.name}</p>
          <span className={cn("rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide", badge.cls)}>
            {badge.label}
          </span>
        </div>
        <p className="mt-0.5 text-[12px] text-muted-foreground">
          Stops at: <strong className="text-foreground">{profile.cutoffQuality}</strong>
          {allowedCount > 0 && ` · ${allowedCount} quality tier${allowedCount === 1 ? "" : "s"} allowed`}
          {cfCount > 0 && ` · ${cfCount} custom format${cfCount === 1 ? "" : "s"}`}
        </p>
      </div>

      {/* Delete */}
      <Button
        variant="ghost"
        size="icon"
        aria-label={`Delete ${profile.name}`}
        onClick={onDelete}
        disabled={busyKey === `delete:${id}`}
        className="shrink-0 text-muted-foreground/50 hover:text-destructive hover:bg-destructive/10"
      >
        {busyKey === `delete:${id}` ? (
          <LoaderCircle className="h-4 w-4 animate-spin" />
        ) : (
          <Trash2 className="h-4 w-4" />
        )}
      </Button>
    </div>
  );
}

/* ── Page ────────────────────────────────────────────────────────── */
export function SettingsProfilesPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { libraries, qualityProfiles, customFormats } = loaderData;
  const revalidator = useRevalidator();
  const [orderedProfiles, setOrderedProfiles] = useState<QualityProfileItem[]>(qualityProfiles);
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [showWizard, setShowWizard] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<QualityProfileItem | null>(null);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates })
  );

  async function handleDragEnd(event: DragEndEvent) {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIndex = orderedProfiles.findIndex((p) => p.id === active.id);
    const newIndex = orderedProfiles.findIndex((p) => p.id === over.id);
    const reordered = arrayMove(orderedProfiles, oldIndex, newIndex);
    setOrderedProfiles(reordered);
    try {
      await authedFetch("/api/quality-profiles/order", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ids: reordered.map((p) => p.id) })
      });
      toast.success("Profile order saved");
    } catch {
      toast.error("Could not save profile order");
    }
  }

  async function confirmDelete() {
    if (!deleteTarget) return;
    const profileId = deleteTarget.id;
    setDeleteTarget(null);
    setBusyKey(`delete:${profileId}`);
    try {
      const res = await authedFetch(`/api/quality-profiles/${profileId}`, { method: "DELETE" });
      if (!res.ok && res.status !== 204) throw new Error("Profile could not be removed.");
      toast.success("Profile removed");
      setOrderedProfiles((prev) => prev.filter((p) => p.id !== profileId));
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Could not remove profile.");
    } finally {
      setBusyKey(null);
    }
  }

  async function ensureCustomFormats(draft: ProfileDraft) {
    const existingByTrashId = new Map(
      customFormats
        .filter((format) => format.trashId)
        .map((format) => [format.trashId!, format])
    );
    const ids: string[] = [];

    for (const [trashId, score] of draft.activeCFs.entries()) {
      const existing = existingByTrashId.get(trashId);
      if (existing) {
        ids.push(existing.id);
        continue;
      }

      const bundled = findBundledCF(trashId);
      if (!bundled) continue;

      const res = await authedFetch("/api/custom-formats", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: bundled.name,
          mediaType: draft.mediaType === "tv" || draft.mediaType === "anime" ? "tv" : "movies",
          score,
          trashId: bundled.trashId,
          conditions: bundled.patterns.map((pattern) => `regex: ${pattern}`).join("\n"),
          upgradeAllowed: true,
        })
      });
      if (!res.ok) throw new Error(`Could not create format "${bundled.name}".`);
      const created = (await res.json()) as CustomFormatItem;
      ids.push(created.id);
      existingByTrashId.set(trashId, created);
    }

    return ids;
  }

  async function handleWizardSave(draft: ProfileDraft) {
    try {
      const cfIds = await ensureCustomFormats(draft);
      const res = await authedFetch("/api/quality-profiles", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: draft.name,
          mediaType: draft.mediaType,
          cutoffQuality: draft.cutoffQualityId,
          allowedQualities: draft.qualityOrder.join(", "),
          customFormatIds: cfIds.join(", "),
          upgradeUntilCutoff: draft.upgradeAllowed,
          upgradeUnknownItems: false,
        })
      });
      if (!res.ok) throw new Error("Profile could not be created.");
      toast.success(`Profile "${draft.name}" created`);
      setShowWizard(false);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Profile could not be created.");
      throw e;
    }
  }

  return (
    <SettingsShell
      title="Quality Profiles"
      description="Define which quality sources and custom formats Deluno targets. Assign profiles to individual libraries."
    >
      <div className="settings-split settings-split-config-heavy">
        {/* ── Profile list ── */}
        <div className="settings-panel space-y-[calc(var(--field-group-pad)*0.9)]">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="font-semibold text-foreground">Your profiles</h3>
              <p className="text-[12px] text-muted-foreground">
                Drag to reorder. The list order doesn't affect priority — profiles are assigned per-library.
              </p>
            </div>
            <Button onClick={() => setShowWizard(true)} className="gap-2 shrink-0">
              <Sparkles className="h-4 w-4" />
              New profile
            </Button>
          </div>

          {/* Wizard panel */}
          {showWizard && (
            <div className="rounded-2xl border border-primary/25 bg-surface-1 p-6 shadow-[0_0_40px_hsl(var(--primary)/0.08)]">
              <div className="mb-5 flex items-center justify-between">
                <div>
                  <p className="font-semibold text-foreground">Create a quality profile</p>
                  <p className="text-[12px] text-muted-foreground">
                    Based on TRaSH Guide recommendations. No JSON, no YAML, no guides required.
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => setShowWizard(false)}
                  className="rounded-xl p-1.5 text-muted-foreground hover:bg-muted/30 hover:text-foreground"
                >
                  <X className="h-4 w-4" />
                </button>
              </div>
              <QualityProfileWizard
                onSave={handleWizardSave}
                onCancel={() => setShowWizard(false)}
              />
            </div>
          )}

          {orderedProfiles.length > 0 ? (
            <DndContext
              sensors={sensors}
              collisionDetection={closestCenter}
              modifiers={[restrictToVerticalAxis, restrictToParentElement]}
              onDragEnd={(e) => void handleDragEnd(e)}
            >
              <SortableContext
                items={orderedProfiles.map((p) => p.id)}
                strategy={verticalListSortingStrategy}
              >
                <div className="space-y-2.5">
                  {orderedProfiles.map((profile) => (
                    <SortableProfileRow
                      key={profile.id}
                      id={profile.id}
                      profile={profile}
                      customFormats={customFormats}
                      busyKey={busyKey}
                      onDelete={() => setDeleteTarget(profile)}
                    />
                  ))}
                </div>
              </SortableContext>
            </DndContext>
          ) : (
            !showWizard && (
              <div className="flex flex-col items-center gap-4 rounded-2xl border-2 border-dashed border-hairline py-12 text-center">
                <Sparkles className="h-8 w-8 text-muted-foreground/30" />
                <div>
                  <p className="font-medium text-foreground">No profiles yet</p>
                  <p className="mt-1 text-[12px] text-muted-foreground">
                    Create your first profile using a TRaSH preset — takes about 30 seconds.
                  </p>
                </div>
                <Button onClick={() => setShowWizard(true)} className="gap-2">
                  <Plus className="h-4 w-4" />
                  Create first profile
                </Button>
              </div>
            )
          )}
        </div>

        {/* ── Libraries panel ── */}
        <div className="settings-panel space-y-[calc(var(--field-group-pad)*0.9)]">
          <div>
            <h3 className="flex items-center gap-2 font-semibold text-foreground">
              Library assignments
              <HelpTip text="Each library is assigned exactly one quality profile. The profile determines which release qualities and custom format scores are used when searching and importing." />
            </h3>
            <p className="text-[12px] text-muted-foreground">
              Change a library's profile from its library settings.
            </p>
          </div>
          <div className="space-y-2">
            {libraries.map((library) => {
              const matchedProfile = orderedProfiles.find(
                (p) => p.name === library.qualityProfileName
              );
              const badge = matchedProfile
                ? MEDIA_BADGES[matchedProfile.mediaType ?? "movies"] ?? MEDIA_BADGES.movies
                : null;

              return (
                <div key={library.id} className="flex items-center gap-3 rounded-xl border border-hairline bg-surface-1 px-4 py-3">
                  <div className="min-w-0 flex-1">
                    <p className="text-[13px] font-medium text-foreground">{library.name}</p>
                    <p className="text-[11.5px] text-muted-foreground">
                      {library.qualityProfileName ?? "No profile assigned"}
                    </p>
                  </div>
                  {badge && (
                    <span className={cn("rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide", badge.cls)}>
                      {badge.label}
                    </span>
                  )}
                </div>
              );
            })}
            {libraries.length === 0 && (
              <p className="text-[12px] text-muted-foreground">No libraries set up yet.</p>
            )}
          </div>

          {/* Inline guide */}
          <div className="rounded-2xl border border-hairline bg-surface-1 p-4 space-y-3">
            <p className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">How profiles work</p>
            <div className="space-y-2 text-[12px] text-muted-foreground">
              <div className="flex gap-2">
                <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-primary/15 text-[9px] font-bold text-primary">1</span>
                <p><strong className="text-foreground">Quality order</strong> — most preferred to least preferred release source</p>
              </div>
              <div className="flex gap-2">
                <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-primary/15 text-[9px] font-bold text-primary">2</span>
                <p><strong className="text-foreground">Cutoff quality</strong> — stop upgrading once this quality is reached</p>
              </div>
              <div className="flex gap-2">
                <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-primary/15 text-[9px] font-bold text-primary">3</span>
                <p><strong className="text-foreground">Custom formats</strong> — scoring bonuses and penalties applied to candidates</p>
              </div>
              <div className="flex gap-2">
                <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-primary/15 text-[9px] font-bold text-primary">4</span>
                <p><strong className="text-foreground">Min score</strong> — reject candidates scoring below this threshold</p>
              </div>
            </div>
          </div>
        </div>
      </div>

      <ConfirmDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => { if (!open) setDeleteTarget(null); }}
        title={`Delete "${deleteTarget?.name}"?`}
        description="This profile will be permanently removed. Libraries using it will need to be reassigned."
        confirmLabel="Delete profile"
        confirmVariant="destructive"
        busy={busyKey !== null}
        onConfirm={() => void confirmDelete()}
      />
    </SettingsShell>
  );
}
