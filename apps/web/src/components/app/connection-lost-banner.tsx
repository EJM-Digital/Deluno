import { useEffect, useState } from "react";
import { WifiOff } from "lucide-react";
import { useSignalRStatus } from "../../lib/use-signalr";

/** How long Deluno is out of reach before the notice stops promising it will be back on its own and says what to do. */
const GIVE_UP_AFTER_MS = 60_000;

/**
 * One strip over the foot of every screen while the page has lost its live connection to Deluno (#1497), for example while Deluno restarts.
 * The numbers on screen may be out of date until it is back, so it says so in the page itself and not only in the top bar's badge
 * (which is hidden on a narrow screen). It is drawn over the page, not in its flow, so it never pushes the screen down and resizes its panels when the connection drops or returns (#1508). It clears by itself once the connection is back and the screens have read again. If the
 * connection has not come back after a minute it says what to do, so a screen never sits frozen and silent.
 */
export function ConnectionLostBanner() {
  const status = useSignalRStatus();
  const lost = status === "reconnecting" || status === "disconnected";
  const [gaveUp, setGaveUp] = useState(false);

  useEffect(() => {
    if (!lost) {
      setGaveUp(false);
      return;
    }
    // display-clock: switches the wording after a minute down; it fetches no data.
    const timer = globalThis.setTimeout(() => setGaveUp(true), GIVE_UP_AFTER_MS);
    return () => globalThis.clearTimeout(timer);
  }, [lost]);

  if (!lost) return null;

  return (
    <div
      role="status"
      aria-live="polite"
      data-connection-lost={gaveUp ? "stuck" : "reconnecting"}
      className="pointer-events-none fixed inset-x-0 bottom-0 z-110 flex items-center justify-center gap-2 border-t border-warning/40 bg-warning/15 px-4 py-2 text-center text-(length:--type-body-sm) font-medium text-warning-foreground backdrop-blur-sm"
    >
      <WifiOff className="h-4 w-4 shrink-0" aria-hidden />
      <span>
        {gaveUp
          ? "Can’t reach Deluno. Check that it is running, then reload this page; if it keeps failing, sign in again. The numbers on screen are out of date."
          : "Reconnecting to Deluno… the numbers on screen may be out of date until it is back."}
      </span>
    </div>
  );
}
