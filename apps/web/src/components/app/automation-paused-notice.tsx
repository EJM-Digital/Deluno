import { useEffect, useRef, useState } from "react";
import { Loader2, PauseCircle } from "lucide-react";
import { fetchJson, type PlatformSettingsSnapshot } from "../../lib/api";
import { RealtimeGroups, useSignalREvent, useSignalRResync } from "../../lib/use-signalr";
import { authedFetch } from "../../lib/use-auth";
import { Button } from "../ui/button";
import { toast } from "../shell/toaster";

/**
 * Says, in plain words and with a way out, that Automation is paused (#936).
 * Nothing is on by default, and a paused Deluno holds search-on-add, the check
 * on a finished download and every scheduled search. The golden-path run found
 * a film sitting for nine minutes with nothing saying why.
 *
 * It reads the one setting itself, follows the server's `SettingsChanged`
 * push (no timer), and draws nothing while automation is running or while the
 * answer is not known yet.
 */
export function AutomationPausedNotice({ context, noun = "title" }: {
  /** Where it is drawn: the Dashboard says what is held; the add screens say what happens to the title being added. */
  context: "dashboard" | "add";
  /** The kind of title being added: "film", "show" or "title". */
  noun?: string;
}) {
  const [paused, setPaused] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const active = useRef(true);

  useEffect(() => {
    active.current = true;
    return () => { active.current = false; };
  }, []);

  const read = useRef(async () => {
    try {
      const settings = await fetchJson<PlatformSettingsSnapshot>("/api/settings");
      if (active.current && typeof settings.autoStartJobs === "boolean") setPaused(!settings.autoStartJobs);
    } catch {
      // Not knowing says nothing about automation: keep whatever was last known.
    }
  }).current;

  useEffect(() => { void read(); }, [read]);
  useSignalREvent("SettingsChanged", RealtimeGroups.Dashboard, () => { void read(); });
  useSignalRResync(() => { void read(); });

  async function resume() {
    setBusy(true);
    try {
      const response = await authedFetch("/api/settings/automation", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ isEnabled: true }) });
      if (!response.ok) throw new Error("Deluno could not resume automation.");
      if (active.current) setPaused(false);
      toast.success("Automation resumed");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Deluno could not resume automation.");
    } finally {
      if (active.current) setBusy(false);
    }
  }

  if (!paused) return null;

  const text = context === "add"
    ? `Automation is paused, so this ${noun} won't be searched until you resume it.`
    : "Automation is paused, so nothing is searched or sent to your download clients by itself, and finished downloads wait. Resume it when you want Deluno to start.";

  return (
    <div
      role="status"
      data-automation-paused={context}
      className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-xl border border-warning/40 bg-warning/15 px-3 py-2 text-(length:--type-body-sm) text-warning-foreground"
    >
      <PauseCircle className="h-4 w-4 shrink-0" aria-hidden />
      <span className="min-w-0 flex-1">{text}</span>
      <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => void resume()}>
        {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> : null}
        Resume automation
      </Button>
    </div>
  );
}
