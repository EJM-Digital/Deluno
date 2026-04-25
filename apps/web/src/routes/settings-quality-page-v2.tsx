import { useLoaderData } from "react-router-dom";
import { SettingsShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { KpiCard } from "../components/app/kpi-card";
import { settingsOverviewLoader } from "./settings-overview-page";
import { emptyPlatformSettingsSnapshot, type LibraryItem, type PlatformSettingsSnapshot, type QualityProfileItem } from "../lib/api";
import { Clapperboard, Film, ShieldCheck, SlidersHorizontal } from "lucide-react";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export const settingsQualityLoader = settingsOverviewLoader;

export function SettingsQualityPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  const { libraries, qualityProfiles } = loaderData ?? {
    libraries: [],
    qualityProfiles: [],
    settings: emptyPlatformSettingsSnapshot
  };

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
      description="Quality is Deluno's raw acceptance layer: cutoffs, allowed ladders, and upgrade posture that profiles assign to libraries."
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
            <CardTitle>Profile quality matrix</CardTitle>
            <CardDescription>Current cutoff and allowed-quality posture coming from live Deluno profiles.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            {qualityProfiles.map((profile) => (
              <div key={profile.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <p className="font-display text-base font-semibold text-foreground">{profile.name}</p>
                    <p className="mt-1 text-xs uppercase tracking-[0.18em] text-muted-foreground">
                      {profile.mediaType === "tv" ? "TV" : "Movies"} · cutoff {profile.cutoffQuality}
                    </p>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <span className="rounded-full border border-hairline px-2.5 py-1 text-xs text-muted-foreground">
                      Upgrade to cutoff: {profile.upgradeUntilCutoff ? "On" : "Off"}
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
              <CardDescription>Distinct allowed-quality values currently modeled in Deluno.</CardDescription>
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
              <CardTitle>Still missing</CardTitle>
              <CardDescription>The deeper quality model Deluno still needs to fully match and exceed *arr behavior.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 text-sm text-muted-foreground">
              <BacklogRow title="Quality definitions" copy="Editable ladder entries instead of only string-based qualities." />
              <BacklogRow title="Size bounds" copy="Per-quality min/max size rules with real validation in release evaluation." />
              <BacklogRow title="Score ceilings" copy="Upgrade-stop conditions based on quality and future custom-format scoring." />
            </CardContent>
          </Card>
        </div>
      </div>
    </SettingsShell>
  );
}

function BacklogRow({ title, copy }: { title: string; copy: string }) {
  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-4">
      <p className="font-medium text-foreground">{title}</p>
      <p className="mt-1">{copy}</p>
    </div>
  );
}
