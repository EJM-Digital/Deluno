import { useEffect, useState } from "react";
import { useLoaderData } from "react-router-dom";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { KpiCard } from "../components/app/kpi-card";
import { settingsOverviewLoader } from "./settings-overview-page";
import { fetchJson, type LibraryItem, type PlatformSettingsSnapshot, type QualityModelSnapshot, type QualityProfileItem, type QualityTierDefinition } from "../lib/api";
import { Clapperboard, Film, ShieldCheck, SlidersHorizontal } from "lucide-react";
import { RouteSkeleton } from "../components/shell/skeleton";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsQualityLoader = settingsOverviewLoader;

export function SettingsQualityPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { libraries, qualityProfiles } = loaderData;
  const [qualityModel, setQualityModel] = useState<QualityModelSnapshot | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void (async () => {
      const model = await fetchJson<QualityModelSnapshot>("/api/quality-model");
      setQualityModel(model);
    })();
  }, []);

  const uniqueAllowedQualities = Array.from(
    new Set(
      qualityProfiles.flatMap((profile) =>
        profile.allowedQualities
          .split(",")
          .map((item) => item.trim())
          .filter(Boolean)
      )
    )
  ).sort((left, right) => left.localeCompare(right));

  const movieProfiles = qualityProfiles.filter((profile) => profile.mediaType === "movies");
  const tvProfiles = qualityProfiles.filter((profile) => profile.mediaType === "tv");
  const librariesWithProfiles = libraries.filter((library) => library.qualityProfileId).length;

  return (
    <SettingsShell
      title="Quality"
      description="Set the minimum quality you'll accept and when to automatically grab better versions."
    >
      <div className="fluid-kpi-grid">
        <KpiCard
          label="Profiles"
          value={String(qualityProfiles.length)}
          icon={SlidersHorizontal}
          meta="Total quality profiles available to libraries."
          sparkline={[2, 2, 3, 3, 4, 4, 5, 5, 5, 6, 6, 6, 7, 7, 7]}
        />
        <KpiCard
          label="Movie profiles"
          value={String(movieProfiles.length)}
          icon={Film}
          meta="Profiles currently scoped to movie libraries."
          sparkline={[1, 1, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4]}
        />
        <KpiCard
          label="TV profiles"
          value={String(tvProfiles.length)}
          icon={Clapperboard}
          meta="Profiles currently scoped to TV libraries."
          sparkline={[1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 4]}
        />
        <KpiCard
          label="Assigned libraries"
          value={`${librariesWithProfiles}/${libraries.length}`}
          icon={ShieldCheck}
          meta="Libraries already attached to a quality profile."
          sparkline={[2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 5]}
        />
      </div>

      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle>Quality profiles</CardTitle>
            <CardDescription>Your current quality settings — what's allowed and where upgrades stop.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            {qualityProfiles.map((profile) => (
              <div key={profile.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <p className="font-display text-base font-semibold text-foreground">{profile.name}</p>
                    <p className="mt-1 text-xs uppercase tracking-[0.18em] text-muted-foreground">
                      {profile.mediaType === "tv" ? "TV" : "Movies"} · stops at {profile.cutoffQuality}
                    </p>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <span className="rounded-full border border-hairline px-2.5 py-1 text-xs text-muted-foreground">
                      Upgrade until target: {profile.upgradeUntilCutoff ? "On" : "Off"}
                    </span>
                    <span className="rounded-full border border-hairline px-2.5 py-1 text-xs text-muted-foreground">
                      Upgrade unknown: {profile.upgradeUnknownItems ? "On" : "Off"}
                    </span>
                  </div>
                </div>

                <div className="mt-3 flex flex-wrap gap-2">
                  {profile.allowedQualities
                    .split(",")
                    .map((quality) => quality.trim())
                    .filter(Boolean)
                    .map((quality) => (
                      <span
                        key={`${profile.id}:${quality}`}
                        className="rounded-full border border-hairline bg-card px-2.5 py-1 text-xs text-muted-foreground"
                      >
                        {quality}
                      </span>
                    ))}
                </div>
              </div>
            ))}
          </CardContent>
        </Card>

        <div className="settings-side-stack">
          <Card>
            <CardHeader>
              <CardTitle>Quality coverage</CardTitle>
              <CardDescription>All quality levels used across your profiles.</CardDescription>
            </CardHeader>
            <CardContent className="flex flex-wrap gap-2">
              {uniqueAllowedQualities.map((quality) => (
                <span
                  key={quality}
                  className="rounded-full border border-hairline bg-surface-1 px-2.5 py-1 text-xs text-muted-foreground"
                >
                  {quality}
                </span>
              ))}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Library assignment</CardTitle>
              <CardDescription>Where profile selection is active today.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {libraries.map((library) => (
                <div key={library.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                  <p className="font-medium text-foreground">{library.name}</p>
                  <p className="mt-1 text-sm text-muted-foreground">
                    {library.qualityProfileName ?? "No profile assigned"}
                  </p>
                  <p className="mt-1 text-xs text-muted-foreground">
                    Cutoff: {library.cutoffQuality ?? "Not set"} · Auto: {library.autoSearchEnabled ? "On" : "Off"}
                  </p>
                </div>
              ))}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Quality model</CardTitle>
              <CardDescription>Editable quality ladder, movie/episode size bounds, and upgrade-stop policy.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 text-sm text-muted-foreground">
              {message ? (
                <p className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-xs">{message}</p>
              ) : null}
              {qualityModel ? (
                <>
                  <div className="space-y-2">
                    {qualityModel.tiers.map((tier, index) => (
                      <div key={tier.name} className="rounded-xl border border-hairline bg-surface-1 p-3">
                        <p className="mb-2 text-xs font-semibold text-foreground">{tier.name} (rank {tier.rank})</p>
                        <div className="grid grid-cols-2 gap-2">
                          <label className="text-[11px]">
                            Movie min (GB)
                            <Input
                              type="number"
                              value={tier.movieMinGb}
                              onChange={(event) => setQualityModel((current) => updateTierValue(current, index, "movieMinGb", Number(event.target.value || 0)))}
                            />
                          </label>
                          <label className="text-[11px]">
                            Movie max (GB)
                            <Input
                              type="number"
                              value={tier.movieMaxGb}
                              onChange={(event) => setQualityModel((current) => updateTierValue(current, index, "movieMaxGb", Number(event.target.value || 0)))}
                            />
                          </label>
                          <label className="text-[11px]">
                            Episode min (MB)
                            <Input
                              type="number"
                              value={tier.episodeMinMb}
                              onChange={(event) => setQualityModel((current) => updateTierValue(current, index, "episodeMinMb", Number(event.target.value || 0)))}
                            />
                          </label>
                          <label className="text-[11px]">
                            Episode max (MB)
                            <Input
                              type="number"
                              value={tier.episodeMaxMb}
                              onChange={(event) => setQualityModel((current) => updateTierValue(current, index, "episodeMaxMb", Number(event.target.value || 0)))}
                            />
                          </label>
                        </div>
                      </div>
                    ))}
                  </div>
                  <label className="flex items-center gap-2 text-xs text-foreground">
                    <input
                      type="checkbox"
                      checked={qualityModel.upgradeStop.stopWhenCutoffMet}
                      onChange={(event) =>
                        setQualityModel((current) =>
                          current ? { ...current, upgradeStop: { ...current.upgradeStop, stopWhenCutoffMet: event.target.checked } } : current)
                      }
                    />
                    Stop upgrades when current quality already meets cutoff
                  </label>
                  <label className="flex items-center gap-2 text-xs text-foreground">
                    <input
                      type="checkbox"
                      checked={qualityModel.upgradeStop.requireCustomFormatGainForSameQuality}
                      onChange={(event) =>
                        setQualityModel((current) =>
                          current
                            ? { ...current, upgradeStop: { ...current.upgradeStop, requireCustomFormatGainForSameQuality: event.target.checked } }
                            : current)
                      }
                    />
                    Require custom-format score gain for same-quality replacements
                  </label>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={saving}
                    onClick={() => void saveModel(qualityModel, setSaving, setMessage, setQualityModel)}
                  >
                    {saving ? "Saving..." : "Save quality model"}
                  </Button>
                </>
              ) : (
                <p>Loading quality model...</p>
              )}
            </CardContent>
          </Card>
        </div>
      </div>
    </SettingsShell>
  );
}

function updateTierValue(
  model: QualityModelSnapshot | null,
  index: number,
  key: keyof QualityTierDefinition,
  value: number
) {
  if (!model) return model;
  const tiers = model.tiers.map((tier, tierIndex) =>
    tierIndex === index ? { ...tier, [key]: Number.isFinite(value) ? value : 0 } : tier);
  return { ...model, tiers };
}

async function saveModel(
  model: QualityModelSnapshot,
  setSaving: (value: boolean) => void,
  setMessage: (value: string | null) => void,
  setQualityModel: (value: QualityModelSnapshot | null) => void
) {
  setSaving(true);
  setMessage(null);
  try {
    const saved = await fetchJson<QualityModelSnapshot>("/api/quality-model", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        tiers: model.tiers,
        upgradeStop: model.upgradeStop
      })
    });
    setQualityModel(saved);
    setMessage("Quality model saved.");
  } catch (error) {
    setMessage(error instanceof Error ? error.message : "Failed to save quality model.");
  } finally {
    setSaving(false);
  }
}
