import { useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Check, TestTube2 } from "lucide-react";
import { toast } from "sonner";
import { fetchJson, readValidationProblem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerSection } from "../components/ui/drawer";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { LIST_TRACK, ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Switch } from "../components/ui/switch";

const TABS = configurationNavAreas.find((area) => area.to === "/subtitles/languages")?.items ?? [];

/**
 * One subtitle source, as the server describes it.
 *
 * The shape is deliberately "what Deluno ships, plus what you have set up",
 * rather than "the ones you have set up": which sources exist is a fact about
 * the build, and a screen that only lists what you have already added cannot
 * tell you what you are missing.
 */
interface SubtitleProviderOption {
  key: string;
  displayName: string;
  description: string;
  scope: "both" | "movies" | "tv";
  needsUsername: boolean;
  needsPassword: boolean;
  needsApiKey: boolean;
  credentialsOptional: boolean;
  configured: {
    id: string;
    providerKey: string;
    name: string;
    username: string | null;
    hasSecret: boolean;
    hasApiKey: boolean;
    priority: number;
    isEnabled: boolean;
    healthStatus: string;
    lastHealthMessage: string | null;
    lastHealthLatencyMs: number | null;
    lastHealthTestUtc: string | null;
    consecutiveFailures: number;
    rateLimitedUntilUtc: string | null;
  } | null;
}

export async function subtitleProvidersLoader() {
  return { providers: await fetchJson<SubtitleProviderOption[]>("/api/subtitle-providers") };
}

const SCOPE_LABEL: Record<SubtitleProviderOption["scope"], string> = {
  both: "Films and TV",
  movies: "Films only",
  tv: "TV only"
};

/**
 * Where subtitles come from.
 *
 * <p>Seven sources, each saying plainly what an account buys you — rather than
 * the forty Bazarr offers with no way to tell which of them still work. Two need
 * nothing at all, and they are first, so somebody can watch the whole loop work
 * before deciding whether to sign up for anything.</p>
 */
export function SubtitleProvidersPage() {
  const { providers } = useLoaderData() as { providers: SubtitleProviderOption[] };
  const revalidator = useRevalidator();

  const [editing, setEditing] = useState<SubtitleProviderOption | null>(null);
  const [form, setForm] = useState({ username: "", secret: "", apiKey: "", priority: 100, isEnabled: true });
  const [busy, setBusy] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});

  const configured = providers.filter((provider) => provider.configured);

  function open(provider: SubtitleProviderOption) {
    setEditing(provider);
    setErrors({});
    setForm({
      username: provider.configured?.username ?? "",
      // Never prefilled: the server does not send them back, and a box that
      // looks full but is not is worse than an empty one. Blank means "keep
      // what is saved", which the endpoint honours.
      secret: "",
      apiKey: "",
      priority: provider.configured?.priority ?? 100,
      isEnabled: provider.configured?.isEnabled ?? true
    });
  }

  async function save() {
    if (!editing) return;
    setBusy("save");
    setErrors({});
    try {
      const response = await authedFetch(`/api/subtitle-providers/${editing.key}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ providerKey: editing.key, ...form })
      });

      if (!response.ok) {
        const problem = await readValidationProblem(response.clone()).catch(() => null);
        if (problem?.errors) {
          setErrors(Object.fromEntries(Object.entries(problem.errors).map(([key, value]) => [key, value[0] ?? ""])));
          return;
        }
        throw new Error("That could not be saved.");
      }

      toast.success(`${editing.displayName} saved`);
      setEditing(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That could not be saved.");
    } finally {
      setBusy(null);
    }
  }

  async function test() {
    if (!editing) return;
    setBusy("test");
    try {
      const response = await authedFetch(`/api/subtitle-providers/${editing.key}/test`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ providerKey: editing.key, ...form })
      });

      const result = await response.json() as { ok: boolean; message: string };
      // Both outcomes are said out loud. A provider answering 200 with an empty
      // list because the key is wrong is the failure people actually hit, and
      // "connected" would be a lie about it.
      if (result.ok) toast.success(result.message);
      else toast.error(result.message);
      revalidator.revalidate();
    } catch {
      toast.error("The test could not be run.");
    } finally {
      setBusy(null);
    }
  }

  async function toggle(provider: SubtitleProviderOption, isEnabled: boolean) {
    setBusy(`toggle:${provider.key}`);
    try {
      await authedFetch(`/api/subtitle-providers/${provider.key}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          providerKey: provider.key,
          username: provider.configured?.username ?? null,
          priority: provider.configured?.priority ?? 100,
          isEnabled
        })
      });
      revalidator.revalidate();
    } catch {
      toast.error(`${provider.displayName} could not be changed.`);
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={TABS} />

      <ListCard
        title="Subtitle providers"
        count={configured.length ? `${configured.length} of ${providers.length} set up` : undefined}
      >
        {providers.length === 0 ? (
          <ListEmpty
            title="No subtitle providers"
            description="This build ships none, which should not happen. Check the logs."
          />
        ) : (
          <ListTable
            columns={[
              { label: "Provider" },
              { label: "Covers" },
              { label: "Account" },
              { label: "Last result" },
              { label: "Status", width: LIST_TRACK.status, mobile: true },
              { label: "On", width: LIST_TRACK.toggle, mobile: true }
            ]}
          >
            {providers.map((provider) => (
              <ListRow key={provider.key} onClick={() => open(provider)} selected={editing?.key === provider.key}>
                <ListNameCell name={provider.displayName} sub={provider.description} />
                <ListCell primary={SCOPE_LABEL[provider.scope]} secondary={provider.configured ? `Priority ${provider.configured.priority}` : undefined} />
                <ListCell primary={accountLabel(provider)} secondary={accountDetail(provider)} />
                <ListCell
                  primary={provider.configured?.lastHealthTestUtc ? relative(provider.configured.lastHealthTestUtc) : "Never tested"}
                  secondary={provider.configured?.lastHealthMessage ?? undefined}
                />
                <ListCell mobile>
                  <Chip tone={healthTone(provider)}>{healthLabel(provider)}</Chip>
                </ListCell>
                <ListCell mobile>
                  <Switch
                    size="sm"
                    aria-label={`${provider.configured?.isEnabled ? "Pause" : "Enable"} ${provider.displayName}`}
                    checked={provider.configured?.isEnabled ?? false}
                    disabled={busy === `toggle:${provider.key}` || (!provider.configured && needsSetup(provider))}
                    onCheckedChange={(checked) => void toggle(provider, checked)}
                  />
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={editing !== null}
        onOpenChange={(open) => !open && setEditing(null)}
        title={editing?.displayName ?? "Provider"}
        description={editing?.description}
        footer={
          <>
            <span className="text-[length:var(--type-caption)] text-muted-foreground">
              {editing?.configured ? `Last result: ${editing.configured.lastHealthMessage ?? "not tested yet"}` : "Not set up yet."}
            </span>
            <div className="flex gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => void test()} disabled={busy !== null} className="gap-1.5">
                <TestTube2 className="h-3.5 w-3.5" />
                {busy === "test" ? "Testing…" : "Test"}
              </Button>
              <Button type="button" size="sm" onClick={() => void save()} disabled={busy !== null} className="gap-1.5">
                <Check className="h-3.5 w-3.5" />
                {busy === "save" ? "Saving…" : "Save"}
              </Button>
            </div>
          </>
        }
      >
        {editing ? (
          <DrawerSection title="Account">
            {!editing.needsUsername && !editing.needsPassword && !editing.needsApiKey ? (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">
                {editing.displayName} needs no account at all. Turn it on and Deluno will start asking it.
              </p>
            ) : (
              <>
                {editing.credentialsOptional ? (
                  <p className="text-[length:var(--type-caption)] text-muted-foreground">
                    Optional. {editing.displayName} answers without an account and answers more often with one.
                  </p>
                ) : null}

                {editing.needsUsername ? (
                  <Field label="Username" error={errors.username}>
                    <Input value={form.username} onChange={(event) => setForm({ ...form, username: event.target.value })} />
                  </Field>
                ) : null}

                {editing.needsPassword ? (
                  <Field
                    label="Password"
                    // Said rather than shown: the server never sends a saved
                    // secret back, so an empty box that already has one behind
                    // it has to explain itself.
                    help={editing.configured?.hasSecret ? "A password is saved. Leave this blank to keep it." : undefined}
                    error={errors.secret}
                  >
                    <Input type="password" value={form.secret} onChange={(event) => setForm({ ...form, secret: event.target.value })} />
                  </Field>
                ) : null}

                {editing.needsApiKey ? (
                  <Field
                    label="API key"
                    help={editing.configured?.hasApiKey ? "A key is saved. Leave this blank to keep it." : undefined}
                    error={errors.apiKey}
                  >
                    <Input type="password" value={form.apiKey} onChange={(event) => setForm({ ...form, apiKey: event.target.value })} />
                  </Field>
                ) : null}
              </>
            )}

            <Field label="Priority" help="Lower is asked first. Deluno stops at the first provider that has the subtitle.">
              <Input
                type="number"
                value={form.priority}
                onChange={(event) => setForm({ ...form, priority: Number(event.target.value) || 100 })}
                className="w-28"
              />
            </Field>

            <Field label="Enabled" help="A provider that is off is never asked.">
              <Switch checked={form.isEnabled} onCheckedChange={(isEnabled) => setForm({ ...form, isEnabled })} />
            </Field>
          </DrawerSection>
        ) : null}
      </Drawer>
    </div>
  );
}

function needsSetup(provider: SubtitleProviderOption) {
  return !provider.credentialsOptional && (provider.needsApiKey || provider.needsUsername || provider.needsPassword);
}

function accountLabel(provider: SubtitleProviderOption) {
  if (!provider.needsApiKey && !provider.needsUsername && !provider.needsPassword) return "None needed";
  return provider.credentialsOptional ? "Optional" : "Required";
}

function accountDetail(provider: SubtitleProviderOption) {
  if (!provider.configured) return undefined;
  const held = [
    provider.configured.username ? "username" : null,
    provider.configured.hasSecret ? "password" : null,
    provider.configured.hasApiKey ? "key" : null
  ].filter(Boolean);
  return held.length ? `Saved: ${held.join(", ")}` : "Nothing saved yet";
}

/**
 * Rate limited is not unhealthy.
 *
 * A source that is working and has asked to be left alone reads as caution, not
 * failure — the same distinction an indexer already draws, and the reason the
 * fetcher records it as a success.
 */
function healthTone(provider: SubtitleProviderOption): "ok" | "warn" | "bad" | "idle" {
  if (!provider.configured) return "idle";
  if (!provider.configured.isEnabled) return "idle";
  switch (provider.configured.healthStatus) {
    case "healthy": return "ok";
    case "rate-limited":
    case "degraded": return "warn";
    case "failed": return "bad";
    default: return "idle";
  }
}

function healthLabel(provider: SubtitleProviderOption) {
  if (!provider.configured) return "Not set up";
  if (!provider.configured.isEnabled) return "Off";
  switch (provider.configured.healthStatus) {
    case "healthy": return "Working";
    case "rate-limited": return "Rate limited";
    case "degraded": return "Answering, no results";
    case "failed": return "Not reachable";
    default: return "Untested";
  }
}

function relative(iso: string) {
  const then = new Date(iso).getTime();
  const minutes = Math.round((Date.now() - then) / 60000);
  if (minutes < 1) return "Just now";
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  return hours < 24 ? `${hours} h ago` : `${Math.round(hours / 24)} d ago`;
}
