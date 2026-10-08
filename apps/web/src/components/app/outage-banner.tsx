import { useEffect, useRef, useState } from "react";
import { WifiOff } from "lucide-react";
import { fetchJson } from "../../lib/api";
import { RealtimeGroups, useSignalREvent, useSignalRResync } from "../../lib/use-signalr";

export type ConnectivityStatus = {
  isOutage: boolean;
  sinceUtc: string | null;
  checkedUtc: string;
  checkedHosts: string[];
  message: string;
};

function formatSince(sinceUtc: string | null): string {
  if (!sinceUtc) return "";
  const date = new Date(sinceUtc);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

/**
 * One app-wide strip while the internet is down (#901): cleanup and searching
 * pause everywhere Deluno would otherwise judge a download, and this is the
 * one place that says so. It is read once when the app opens, then follows the
 * server's `ConnectivityChanged` push the moment the state flips (#1179), with
 * no timer; it clears itself the moment the connection is back — nothing to
 * dismiss, because there is nothing left to say once it is gone.
 */
export function OutageBanner() {
  const [status, setStatus] = useState<ConnectivityStatus | null>(null);
  const active = useRef(true);

  useEffect(() => {
    active.current = true;
    return () => { active.current = false; };
  }, []);

  const read = useRef(async () => {
    try {
      const next = await fetchJson<ConnectivityStatus>("/api/system/connectivity");
      if (active.current) setStatus(next);
    } catch {
      // A failed poll says nothing about connectivity; the banner just
      // keeps whatever it last knew until the next successful check.
    }
  }).current;

  useEffect(() => { void read(); }, [read]);

  // The server says when the state flips, even while Deluno sits hidden or minimised.
  useSignalREvent("ConnectivityChanged", RealtimeGroups.Dashboard, (event) => {
    setStatus((current) => ({
      isOutage: event.isOutage,
      sinceUtc: event.sinceUtc,
      checkedUtc: event.checkedUtc,
      checkedHosts: current?.checkedHosts ?? [],
      message: event.message
    }));
  });
  // Events that could not be replayed after a dropped connection: ask once.
  useSignalRResync(() => void read());

  if (!status || !status.isOutage) {
    return null;
  }

  const since = formatSince(status.sinceUtc);

  return (
    <div
      role="status"
      className="flex items-center justify-center gap-2 border-b border-warning/40 bg-warning/15 px-4 py-2 text-center text-(length:--type-body-sm) font-medium text-warning-foreground"
    >
      <WifiOff className="h-4 w-4 shrink-0" aria-hidden />
      <span>
        {since ? `Internet down since ${since}` : "Internet down"} — cleanup and searching paused until it&apos;s back.
      </span>
    </div>
  );
}
