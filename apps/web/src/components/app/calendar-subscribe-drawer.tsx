/**
 * Subscribe to the schedule — the Sonarr/Radarr iCal feed, without the manual
 * URL assembly those tools ask for (#260).
 *
 * The feed authenticates with an API key in the query string, because no
 * calendar client can send a header. Deluno stores only a hash of a key, so the
 * URL can be shown exactly once: after that the drawer can say a link exists,
 * but not what it is. Replacing it is therefore a real choice, not a refresh,
 * and the drawer says so.
 *
 * Contracts: GET/POST /api/api-keys, DELETE /api/api-keys/{id},
 * GET /api/calendar/feed.ics.
 */
import { useCallback, useEffect, useState } from "react";
import { CalendarPlus, Check, Copy, LoaderCircle } from "lucide-react";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../ui/drawer";
import { Input } from "../ui/input";
import { SwitchRow } from "../ui/switch";
import { authedFetch } from "../../lib/use-auth";
import { fetchJson, type ApiKeyItem, type CreatedApiKeyResponse } from "../../lib/api";

/** The name Deluno gives its own calendar key, so the drawer can find it again. */
const KEY_NAME = "Calendar subscription";

export function CalendarSubscribeDrawer({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const [existing, setExisting] = useState<ApiKeyItem[] | null>(null);
  const [url, setUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  // #786: Radarr/Sonarr's own feed options — off by default, same as the
  // calendar's own unmonitored filter everywhere else in Deluno.
  const [includeUnmonitored, setIncludeUnmonitored] = useState(false);
  const [tagFilter, setTagFilter] = useState("");

  const load = useCallback(async () => {
    try {
      const keys = await fetchJson<ApiKeyItem[]>("/api/api-keys");
      setExisting(keys.filter((key) => key.name === KEY_NAME));
    } catch {
      setExisting([]);
    }
  }, []);

  useEffect(() => {
    if (!open) return;
    setUrl(null);
    setCopied(false);
    setExisting(null);
    void load();
  }, [open, load]);

  async function handleCreate() {
    setBusy(true);
    try {
      // One link at a time: a replacement revokes the old one rather than
      // leaving keys behind that nobody can identify later.
      for (const key of existing ?? []) {
        await authedFetch(`/api/api-keys/${key.id}`, { method: "DELETE" });
      }

      const created = await fetchJson<CreatedApiKeyResponse>("/api/api-keys", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: KEY_NAME, scopes: "read" })
      });

      const params = new URLSearchParams({ apikey: created.apiKey });
      if (includeUnmonitored) params.set("unmonitored", "true");
      const tags = tagFilter
        .split(",")
        .map((tag) => tag.trim())
        .filter((tag) => tag.length > 0);
      if (tags.length > 0) params.set("tag", tags.join(","));

      setUrl(`${window.location.origin}/api/calendar/feed.ics?${params.toString()}`);
      setExisting([created.item]);
      toast.success("Calendar link ready");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Calendar link could not be created.");
    } finally {
      setBusy(false);
    }
  }

  async function handleCopy() {
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      toast.success("Copied to clipboard");
    } catch {
      toast.error("The browser would not copy it. Select the address and copy it by hand.");
    }
  }

  const hasLink = (existing?.length ?? 0) > 0;

  return (
    <Drawer
      open={open}
      onOpenChange={onOpenChange}
      title="Subscribe to the schedule"
      description="Air dates and release dates in your own calendar app"
      footer={<DrawerFooter readOnly state="clean" saveLabel="Close" onCancel={() => onOpenChange(false)} />}
    >
      <DrawerSection title="What you get">
        <p className="density-help leading-relaxed text-muted-foreground">
          A read-only feed of every episode air date and movie release date Deluno knows about — the previous 30 days and the
          next 90. Your calendar app refreshes it on its own schedule, so new episodes appear without you doing anything.
        </p>
      </DrawerSection>

      {url ? (
        <DrawerSection title="Your calendar link" aside="shown once">
          <div className="flex min-w-0 flex-col gap-2 sm:flex-row">
            <code className="min-w-0 flex-1 overflow-x-auto rounded-[10px] border border-hairline bg-surface-1 p-3 font-mono text-(length:--type-caption) text-foreground">
              {url}
            </code>
            <Button type="button" variant="outline" className="shrink-0" onClick={() => void handleCopy()}>
              {copied ? <Check className="h-4 w-4" /> : <Copy className="h-4 w-4" />}
              {copied ? "Copied" : "Copy"}
            </Button>
          </div>
          <p className="density-help leading-relaxed text-muted-foreground">
            Copy it now. Deluno keeps only a hash of the key inside it, so this is the only time the link can be shown. Treat
            it like a password: anyone with the link can read your schedule.
          </p>
        </DrawerSection>
      ) : (
        <DrawerSection title={hasLink ? "You already have a link" : "Create your link"}>
          <p className="density-help leading-relaxed text-muted-foreground">
            {existing === null
              ? "Checking for an existing link…"
              : hasLink
                ? "A calendar link already exists but cannot be shown again. Creating a new one revokes it, so any calendar already subscribed will stop updating until you give it the new link."
                : "Deluno will create a read-only key and build the link for you. You can revoke it at any time from System → API."}
          </p>
          <SwitchRow
            label="Include unmonitored titles"
            description="Off by default: Deluno is not watching for these, so most people do not want them cluttering a calendar."
            checked={includeUnmonitored}
            onCheckedChange={setIncludeUnmonitored}
          />
          <div className="grid gap-1.5">
            <label htmlFor="calendar-feed-tags" className="density-help font-medium text-foreground">
              Only these tags (optional)
            </label>
            <Input
              id="calendar-feed-tags"
              value={tagFilter}
              onChange={(event) => setTagFilter(event.target.value)}
              placeholder="e.g. Anime, Kids"
            />
            <p className="density-help leading-relaxed text-muted-foreground">
              Comma-separated tag names. Leave blank for every tag.
            </p>
          </div>
          <div>
            <Button type="button" disabled={busy || existing === null} onClick={() => void handleCreate()}>
              {busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <CalendarPlus className="h-4 w-4" />}
              {hasLink ? "Replace calendar link" : "Create calendar link"}
            </Button>
          </div>
        </DrawerSection>
      )}

      <DrawerSection title="Adding it to a calendar">
        <DrawerFacts
          items={[
            { label: "Google Calendar", value: "Other calendars → From URL" },
            { label: "Apple Calendar", value: "File → New Calendar Subscription" },
            { label: "Outlook", value: "Add calendar → Subscribe from web" }
          ]}
        />
        <p className="density-help leading-relaxed text-muted-foreground">
          The link only works from a device that can reach this Deluno. On your home network that means the same address you
          use in the browser.
        </p>
      </DrawerSection>
    </Drawer>
  );
}
