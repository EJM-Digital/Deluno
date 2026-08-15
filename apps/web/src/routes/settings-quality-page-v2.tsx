import { useState } from "react";
import { useLoaderData } from "react-router-dom";
import { SettingsShell } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { RouteSkeleton } from "../components/shell/skeleton";
import { fetchJson, type QualityModelSnapshot, type QualityTierDefinition } from "../lib/api";

interface QualityLoaderData {
  qualityModel: QualityModelSnapshot;
}

export async function settingsQualityLoader(): Promise<QualityLoaderData> {
  return { qualityModel: await fetchJson<QualityModelSnapshot>("/api/quality-model") };
}

export function SettingsQualityPage() {
  const loaderData = useLoaderData() as QualityLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;

  const [qualityModel, setQualityModel] = useState(loaderData.qualityModel);
  const [message, setMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  return (
    <SettingsShell
      title="Quality & size limits"
      description="Set the practical file-size bounds and upgrade-stop rules Deluno uses after a quality profile has chosen what is acceptable."
    >
      <Card className="settings-panel">
        <CardHeader>
          <CardTitle>Quality tiers</CardTitle>
          <CardDescription>
            These are safety bounds, not another quality profile. A Media Plan and its quality profile decide which tiers are allowed; these limits reject implausibly small or large files in those tiers.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-[var(--page-gap)]">
          {message ? <p className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-sm text-muted-foreground">{message}</p> : null}

          <div className="space-y-3">
            {qualityModel.tiers.map((tier, index) => (
              <section key={tier.name} className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
                <div className="mb-4 flex flex-wrap items-baseline justify-between gap-2">
                  <h2 className="font-semibold text-foreground">{tier.name}</h2>
                  <span className="text-xs text-muted-foreground">Quality rank {tier.rank}</span>
                </div>
                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                  <SizeLimitField label="Movie minimum" unit="GB" value={tier.movieMinGb} onChange={(value) => setQualityModel((current) => updateTierValue(current, index, "movieMinGb", value))} />
                  <SizeLimitField label="Movie maximum" unit="GB" value={tier.movieMaxGb} onChange={(value) => setQualityModel((current) => updateTierValue(current, index, "movieMaxGb", value))} />
                  <SizeLimitField label="Episode minimum" unit="MB" value={tier.episodeMinMb} onChange={(value) => setQualityModel((current) => updateTierValue(current, index, "episodeMinMb", value))} />
                  <SizeLimitField label="Episode maximum" unit="MB" value={tier.episodeMaxMb} onChange={(value) => setQualityModel((current) => updateTierValue(current, index, "episodeMaxMb", value))} />
                </div>
              </section>
            ))}
          </div>

          <section className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
            <h2 className="font-semibold text-foreground">Upgrade stop rules</h2>
            <p className="mt-1 text-sm text-muted-foreground">Choose when Deluno should stop looking for a replacement after a title is already imported.</p>
            <div className="mt-4 grid gap-3 sm:grid-cols-2">
              <ToggleField
                label="Stop at the profile cutoff"
                description="Stop upgrades when the current file has reached the quality profile’s target tier."
                checked={qualityModel.upgradeStop.stopWhenCutoffMet}
                onChange={(checked) => setQualityModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, stopWhenCutoffMet: checked } }))}
              />
              <ToggleField
                label="Require a score improvement at the same quality"
                description="Only replace an equally ranked release when its release-scoring result is better."
                checked={qualityModel.upgradeStop.requireCustomFormatGainForSameQuality}
                onChange={(checked) => setQualityModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, requireCustomFormatGainForSameQuality: checked } }))}
              />
            </div>
          </section>

          <Button type="button" disabled={saving} onClick={() => void saveModel(qualityModel, setSaving, setMessage, setQualityModel)}>
            {saving ? "Saving…" : "Save quality & size limits"}
          </Button>
        </CardContent>
      </Card>
    </SettingsShell>
  );
}

function SizeLimitField({ label, unit, value, onChange }: { label: string; unit: string; value: number; onChange: (value: number) => void }) {
  return (
    <label className="block text-sm font-medium text-foreground">
      {label} ({unit})
      <Input className="mt-2" type="number" min={0} value={value} onChange={(event) => onChange(Number(event.target.value || 0))} />
    </label>
  );
}

function ToggleField({ checked, description, label, onChange }: { checked: boolean; description: string; label: string; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex items-start gap-3 rounded-xl border border-hairline bg-background/40 p-3 text-foreground">
      <input className="mt-1" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span>
        <span className="block text-sm font-semibold">{label}</span>
        <span className="mt-1 block text-sm leading-relaxed text-muted-foreground">{description}</span>
      </span>
    </label>
  );
}

function updateTierValue(model: QualityModelSnapshot, index: number, key: keyof QualityTierDefinition, value: number): QualityModelSnapshot {
  const tiers = model.tiers.map((tier, tierIndex) => tierIndex === index ? { ...tier, [key]: Number.isFinite(value) ? value : 0 } : tier);
  return { ...model, tiers };
}

async function saveModel(
  model: QualityModelSnapshot,
  setSaving: (value: boolean) => void,
  setMessage: (value: string | null) => void,
  setQualityModel: (value: QualityModelSnapshot) => void
) {
  setSaving(true);
  setMessage(null);
  try {
    const saved = await fetchJson<QualityModelSnapshot>("/api/quality-model", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ tiers: model.tiers, upgradeStop: model.upgradeStop })
    });
    setQualityModel(saved);
    setMessage("Quality and size limits saved.");
  } catch (error) {
    setMessage(error instanceof Error ? error.message : "Could not save quality and size limits.");
  } finally {
    setSaving(false);
  }
}
