/**
 * Why a drawer's Remove… is unavailable, in plain words (#1248). A button that is dimmed with nothing said looks broken,
 * and a click on it does nothing. Null when nothing else is running.
 */
export function removeUnavailableReason(busy: string | null): string | null {
  if (busy === null) return null;
  if (busy === "test:form") return "Waiting for the connection test to finish";
  if (busy === "save") return "Waiting for the save to finish";
  if (busy === "remove") return "Removing…";
  if (busy.startsWith("reset:")) return "Waiting for the reset to finish";
  return "Waiting for the current change to finish";
}
