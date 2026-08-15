import { useMemo, useState, type FormEvent, type ReactNode } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { FolderOpen, LoaderCircle, Plus, Tv, Video } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { InputDescription } from "../components/ui/input-description";
import { PathInput } from "../components/ui/path-input";
import { toast } from "../components/shell/toaster";
import { emptyPlatformSettingsSnapshot, fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type PolicySetItem, type QualityProfileItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

interface LoaderData {
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  qualityProfiles: QualityProfileItem[];
  policySets: PolicySetItem[];
}

interface LibraryForm {
  name: string;
  mediaType: "movies" | "tv";
  rootPath: string;
  downloadsPath: string;
  qualityProfileId: string;
}

export async function settingsLibrariesLoader(): Promise<LoaderData> {
  const [libraries, settings, qualityProfiles, policySets] = await Promise.all([
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => emptyPlatformSettingsSnapshot),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles"),
    fetchJson<PolicySetItem[]>("/api/policy-sets")
  ]);
  return { libraries, settings, qualityProfiles, policySets };
}

export function SettingsLibrariesPage() {
  const data = useLoaderData() as LoaderData | undefined;
  if (!data) return <RouteSkeleton />;

  const { libraries, settings, qualityProfiles, policySets } = data;
  const revalidator = useRevalidator();
  const [form, setForm] = useState<LibraryForm>(() => newLibraryForm("movies", settings));
  const [busy, setBusy] = useState(false);
  const [assignmentBusyId, setAssignmentBusyId] = useState<string | null>(null);
  const profiles = useMemo(
    () => qualityProfiles.filter((profile) => profile.mediaType === form.mediaType),
    [form.mediaType, qualityProfiles]
  );
  const hasSelectableProfiles = profiles.length > 0;

  function chooseType(mediaType: "movies" | "tv") {
    setForm((current) => ({
      ...current,
      mediaType,
      name: current.name.trim() ? current.name : mediaType === "movies" ? "Movies" : "TV Shows",
      rootPath: current.rootPath.trim() ? current.rootPath : mediaType === "movies" ? settings.movieRootPath ?? "" : settings.seriesRootPath ?? "",
      qualityProfileId: ""
    }));
  }

  async function createLibrary(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    try {
      const response = await authedFetch("/api/libraries", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: form.name.trim(),
          mediaType: form.mediaType,
          purpose: "Main library",
          rootPath: form.rootPath.trim(),
          downloadsPath: form.downloadsPath.trim() || null,
          qualityProfileId: form.qualityProfileId || null,
          autoSearchEnabled: true,
          missingSearchEnabled: true,
          upgradeSearchEnabled: true,
          searchIntervalHours: 12,
          retryDelayHours: 6,
          maxItemsPerRun: 10
        })
      });
      if (!response.ok) throw new Error(await response.text().catch(() => "Library could not be created."));
      toast.success(`${form.name.trim() || "Library"} is ready`);
      setForm(newLibraryForm(form.mediaType, settings));
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Library could not be created.");
    } finally {
      setBusy(false);
    }
  }

  async function assignQualityProfile(library: LibraryItem, qualityProfileId: string) {
    setAssignmentBusyId(library.id);
    try {
      const response = await authedFetch(`/api/libraries/${library.id}/quality-profile`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ qualityProfileId })
      });
      if (!response.ok) throw new Error("Quality profile could not be assigned.");
      toast.success(`${library.name} now uses the selected quality profile`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Quality profile could not be assigned.");
    } finally {
      setAssignmentBusyId(null);
    }
  }

  async function assignMediaPlan(library: LibraryItem, policySetId: string) {
    setAssignmentBusyId(library.id);
    try {
      const response = await authedFetch(`/api/libraries/${library.id}/media-plan`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ policySetId: policySetId || null })
      });
      if (!response.ok) throw new Error("Media Plan could not be assigned.");
      toast.success(policySetId ? `${library.name} now uses the selected Media Plan` : `${library.name} now uses its standard library settings`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Media Plan could not be assigned.");
    } finally {
      setAssignmentBusyId(null);
    }
  }

  return (
    <SettingsShell
      title="Library folders"
      description="Start here. A library tells Deluno whether it manages movies or TV, where those files live, and which completed-download folder it should watch by default."
    >
      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.12fr)_minmax(22rem,0.88fr)]">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>{libraries.length ? "Add another library" : "Create your first library"}</CardTitle>
            <CardDescription>Most people need one Movies library and one TV library. Keep it simple here; use Final destinations only when you need special genre or tag routes.</CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[var(--field-group-pad)]" onSubmit={createLibrary}>
              <div className="grid gap-[var(--grid-gap)] sm:grid-cols-2">
                <button type="button" onClick={() => chooseType("movies")} className={`rounded-2xl border p-[var(--tile-pad)] text-left transition-colors ${form.mediaType === "movies" ? "border-primary/50 bg-primary/10" : "border-hairline bg-surface-1 hover:border-primary/30"}`}>
                  <Video className="h-5 w-5 text-primary" />
                  <p className="mt-3 font-semibold text-foreground">Movies</p>
                  <p className="mt-1 text-sm text-muted-foreground">Films in one main folder.</p>
                </button>
                <button type="button" onClick={() => chooseType("tv")} className={`rounded-2xl border p-[var(--tile-pad)] text-left transition-colors ${form.mediaType === "tv" ? "border-primary/50 bg-primary/10" : "border-hairline bg-surface-1 hover:border-primary/30"}`}>
                  <Tv className="h-5 w-5 text-primary" />
                  <p className="mt-3 font-semibold text-foreground">TV shows</p>
                  <p className="mt-1 text-sm text-muted-foreground">Series and episodes in one main folder.</p>
                </button>
              </div>
              <div className={hasSelectableProfiles ? "grid gap-[var(--grid-gap)] md:grid-cols-2" : undefined}>
                <Field label="Library name" description="A label you will recognise in Deluno.">
                  <Input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} placeholder={form.mediaType === "movies" ? "Movies" : "TV Shows"} />
                </Field>
                {hasSelectableProfiles ? <Field label="Quality profile" description="This is the profile attached to this library. It contains the allowed qualities, upgrade goal, and custom-format rules Deluno uses.">
                  <select value={form.qualityProfileId} onChange={(event) => setForm((current) => ({ ...current, qualityProfileId: event.target.value }))} className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none">
                    <option value="">Use the standard {form.mediaType === "movies" ? "Movies" : "TV"} profile</option>
                    {profiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.name}</option>)}
                  </select>
                </Field> : null}
              </div>
              {!hasSelectableProfiles ? <p className="rounded-xl border border-hairline bg-surface-1 px-4 py-3 text-sm text-muted-foreground">Deluno assigns the standard {form.mediaType === "movies" ? "Movies" : "TV"} quality profile when this library is created. You can change it once profiles are available.</p> : null}
              <Field label="Library folder" description="The final folder where Deluno stores imported files for this library.">
                <PathInput value={form.rootPath} onChange={(rootPath) => setForm((current) => ({ ...current, rootPath }))} browseTitle={`Choose ${form.mediaType === "movies" ? "movies" : "TV"} library folder`} />
              </Field>
              <Field label="Completed downloads folder" description="Optional. In the standard flow, leave this blank and Deluno uses the completed folder from the download client you connect in the next step. Set it only for a library-specific or processed-output folder.">
                <PathInput value={form.downloadsPath} onChange={(downloadsPath) => setForm((current) => ({ ...current, downloadsPath }))} browseTitle="Choose completed downloads folder" />
              </Field>
              <Button type="submit" disabled={busy}>
                {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />}
                Create {form.mediaType === "movies" ? "Movies" : "TV"} library
              </Button>
            </form>
          </CardContent>
        </Card>

        <div className="space-y-[var(--grid-gap)]">
          <Card>
            <CardHeader>
              <CardTitle>Your libraries</CardTitle>
              <CardDescription>These are the main folders Deluno can manage.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {libraries.length ? libraries.map((library) => {
                const effectiveProfile = qualityProfiles.find((profile) => profile.id === library.qualityProfileId);
                const customFormatCount = (effectiveProfile?.customFormatIds ?? "").split(",").map((id) => id.trim()).filter(Boolean).length;
                return (
                  <div key={library.id} className="space-y-3 rounded-xl border border-hairline bg-surface-1 p-4">
                    <div className="flex items-start gap-3"><FolderOpen className="mt-0.5 h-4 w-4 text-primary" /><div><p className="font-semibold text-foreground">{library.name}</p><p className="mt-1 text-sm text-muted-foreground">{library.mediaType === "tv" ? "TV shows" : "Movies"} · {library.rootPath}</p></div></div>
                    <label className="block">
                      <span className="text-xs font-bold uppercase tracking-[0.14em] text-muted-foreground">Quality profile attached to this library</span>
                      <select
                        value={library.qualityProfileId ?? ""}
                        disabled={assignmentBusyId === library.id || Boolean(library.defaultPolicySetId)}
                        onChange={(event) => void assignQualityProfile(library, event.target.value)}
                        className="density-control-text mt-2 h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                      >
                        {qualityProfiles.filter((profile) => profile.mediaType === library.mediaType).map((profile) => <option key={profile.id} value={profile.id}>{profile.name}{profile.cutoffQuality ? ` · target ${profile.cutoffQuality}` : ""}</option>)}
                      </select>
                      <span className="mt-1 block text-xs text-muted-foreground">{library.defaultPolicySetName ? `Managed by the ${library.defaultPolicySetName} advanced plan.` : library.cutoffQuality ? `${library.cutoffQuality} upgrade target · ${customFormatCount} custom-format rule${customFormatCount === 1 ? "" : "s"} included.` : "No quality profile is assigned yet."}</span>
                    </label>
                    <details className="rounded-xl border border-hairline bg-background/40">
                      <summary className="cursor-pointer px-3 py-2.5 text-sm font-medium text-muted-foreground hover:text-foreground">Advanced: apply a reusable Media Plan</summary>
                      <div className="border-t border-hairline px-3 pb-3 pt-2">
                        <select
                          value={library.defaultPolicySetId ?? ""}
                          disabled={assignmentBusyId === library.id}
                          onChange={(event) => void assignMediaPlan(library, event.target.value)}
                          className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                        >
                          <option value="">No advanced plan — use the profile above</option>
                          {policySets.filter((plan) => plan.mediaType === library.mediaType && plan.isEnabled).map((plan) => <option key={plan.id} value={plan.id}>{plan.name}</option>)}
                        </select>
                        <p className="mt-2 text-xs leading-relaxed text-muted-foreground">Use a Media Plan only when this library needs a reusable bundle of quality, search timing, release rules, and routing. Most Movies and TV libraries should simply select a quality profile above.</p>
                      </div>
                    </details>
                  </div>
                );
              }) : <p className="rounded-xl border border-dashed border-hairline px-4 py-5 text-sm text-muted-foreground">No libraries yet. Create the first one on the left.</p>}
            </CardContent>
          </Card>
          <Card>
            <CardHeader><CardTitle>Need a different destination?</CardTitle><CardDescription>Use routing rules only for exceptions, such as Animation going to a separate folder. They do not replace your main library folder.</CardDescription></CardHeader>
            <CardContent><Button asChild variant="outline"><Link to="/settings/destination-rules">Manage final destinations</Link></Button></CardContent>
          </Card>
        </div>
      </div>
    </SettingsShell>
  );
}

function newLibraryForm(mediaType: "movies" | "tv", settings: PlatformSettingsSnapshot): LibraryForm {
  return {
    name: mediaType === "movies" ? "Movies" : "TV Shows",
    mediaType,
    rootPath: mediaType === "movies" ? settings.movieRootPath ?? "" : settings.seriesRootPath ?? "",
    downloadsPath: settings.downloadsPath ?? "",
    qualityProfileId: ""
  };
}

function Field({ label, description, children }: { label: string; description: string; children: ReactNode }) {
  return <label className="grid gap-2"><span className="text-sm font-semibold text-foreground">{label}</span>{children}<InputDescription>{description}</InputDescription></label>;
}
