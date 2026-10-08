import { useEffect, useState } from "react";
import { Loader2, TriangleAlert } from "lucide-react";
import { Button } from "../ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../ui/card";
import { fetchJson, type ComponentRecoveryNotice, type ComponentRecoveryResult } from "../../lib/api";

/**
 * Says when an app Deluno installed had its own folder rebuilt after a reinstall (#1177), and puts it right from here:
 * "Add them back" re-adds what the app had, "Dismiss" clears the notice. Nothing shows when there is nothing to say,
 * or on a host that has no installed apps.
 */
export function ComponentRecoveryNotices() {
  const [notices, setNotices] = useState<ComponentRecoveryNotice[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [said, setSaid] = useState<Record<string, string>>({});

  async function load() {
    try {
      setNotices(await fetchJson<ComponentRecoveryNotice[]>("/api/system/component-recovery"));
    } catch {
      setNotices([]);
    }
  }

  useEffect(() => {
    let active = true;
    void fetchJson<ComponentRecoveryNotice[]>("/api/system/component-recovery")
      .then((next) => { if (active) setNotices(next); })
      .catch(() => { if (active) setNotices([]); });
    return () => { active = false; };
  }, []);

  async function readd(notice: ComponentRecoveryNotice) {
    setBusy(notice.kind);
    try {
      const result = await fetchJson<ComponentRecoveryResult>(`/api/system/component-recovery/${encodeURIComponent(notice.kind)}/readd`, { method: "POST" });
      setSaid((current) => ({ ...current, [notice.kind]: result.message }));
      await load();
    } catch (error) {
      setSaid((current) => ({ ...current, [notice.kind]: error instanceof Error ? error.message : `Deluno could not add the torrents back to ${notice.displayName}.` }));
    } finally {
      setBusy(null);
    }
  }

  async function dismiss(notice: ComponentRecoveryNotice) {
    setBusy(notice.kind);
    try {
      await fetchJson(`/api/system/component-recovery/${encodeURIComponent(notice.kind)}/dismiss`, { method: "POST" });
      await load();
    } catch (error) {
      setSaid((current) => ({ ...current, [notice.kind]: error instanceof Error ? error.message : "That could not be dismissed." }));
    } finally {
      setBusy(null);
    }
  }

  if (notices.length === 0) {
    // A finished re-add leaves one line saying what happened.
    const lines = Object.values(said);
    return lines.length > 0 ? <p role="status" className="text-sm text-muted-foreground">{lines.join(" ")}</p> : null;
  }

  return (
    <>
      {notices.map((notice) => (
        <Card key={notice.kind} role="status" aria-label={`${notice.displayName} was rebuilt`}>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <TriangleAlert className="h-4 w-4 text-warning" aria-hidden />
              {notice.displayName} was set up again
            </CardTitle>
            <CardDescription>{notice.message}</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-wrap items-center gap-3">
            {notice.canReadd ? (
              <Button onClick={() => void readd(notice)} disabled={busy !== null}>
                {busy === notice.kind ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> : null}
                Add them back
              </Button>
            ) : null}
            <Button variant="outline" onClick={() => void dismiss(notice)} disabled={busy !== null}>
              Dismiss
            </Button>
            {said[notice.kind] ? <span className="text-sm text-muted-foreground" role="alert">{said[notice.kind]}</span> : null}
          </CardContent>
        </Card>
      ))}
    </>
  );
}
