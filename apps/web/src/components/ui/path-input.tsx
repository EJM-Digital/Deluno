import { useEffect, useState } from "react";
import {
  AlertTriangle,
  CheckCircle2,
  ChevronLeft,
  Folder,
  FolderCheck,
  FolderOpen,
  HardDrive,
  LoaderCircle,
  Network,
  Server,
  TerminalSquare,
  XCircle
} from "lucide-react";
import { ApiRequestError, fetchJson, type DirectoryBrowseResponse, type PathDiagnosticResponse } from "../../lib/api";
import { cn } from "../../lib/utils";
import { Button } from "./button";
import { Input } from "./input";
import { Sheet, SheetContent } from "./sheet";

interface PathInputProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
  browseTitle?: string;
}

export function PathInput({
  value,
  onChange,
  placeholder,
  className,
  browseTitle = "Choose folder"
}: PathInputProps) {
  const [open, setOpen] = useState(false);
  const [browserPath, setBrowserPath] = useState<string | null>(null);
  const [browserData, setBrowserData] = useState<DirectoryBrowseResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [manualPath, setManualPath] = useState(value);
  const [diagnostic, setDiagnostic] = useState<PathDiagnosticResponse | null>(null);
  const [diagnosticLoading, setDiagnosticLoading] = useState(false);
  const [diagnosticError, setDiagnosticError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    const initialPath = value.trim() || null;
    setBrowserPath(initialPath);
    setManualPath(value);
    setDiagnostic(null);
    setDiagnosticError(null);
  }, [open, value]);

  useEffect(() => {
    if (!open) {
      return;
    }

    let cancelled = false;

    async function load() {
      setLoading(true);
      setError(null);

      try {
        const query = browserPath ? `?path=${encodeURIComponent(browserPath)}` : "";
        const response = await fetchJson<DirectoryBrowseResponse>(`/api/filesystem/directories${query}`);
        if (!cancelled) {
          setBrowserData(response);
          setNotice(null);
        }
      } catch (loadError) {
        if (
          browserPath &&
          loadError instanceof ApiRequestError &&
          (loadError.status === 400 || loadError.status === 403 || loadError.status === 404)
        ) {
          try {
            const roots = await fetchJson<DirectoryBrowseResponse>("/api/filesystem/directories");
            if (!cancelled) {
              setBrowserData(roots);
              setError(null);
              setNotice(`"${browserPath}" could not be opened. Showing available drives instead.`);
            }
            return;
          } catch {
            // Fall through to the normal error state if even roots cannot be loaded.
          }
        }

        if (!cancelled) {
          setBrowserData(null);
          setError(loadError instanceof Error ? loadError.message : "Folder browser unavailable.");
          setNotice(null);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    void load();

    return () => {
      cancelled = true;
    };
  }, [open, browserPath]);

  function handleSelect(path: string) {
    onChange(path);
    setOpen(false);
  }

  function handleManualUse() {
    const nextPath = manualPath.trim();
    if (!nextPath) {
      return;
    }

    handleSelect(nextPath);
  }

  async function handleDiagnostic(pathValue = manualPath) {
    const nextPath = pathValue.trim();
    if (!nextPath) {
      setDiagnostic(null);
      setDiagnosticError("Enter a path before checking it.");
      return;
    }

    setDiagnosticLoading(true);
    setDiagnosticError(null);

    try {
      const result = await fetchJson<PathDiagnosticResponse>("/api/filesystem/path-diagnostics", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ path: nextPath })
      });
      setDiagnostic(result);
    } catch (checkError) {
      setDiagnostic(null);
      setDiagnosticError(checkError instanceof Error ? checkError.message : "Path check failed.");
    } finally {
      setDiagnosticLoading(false);
    }
  }

  const dockerPresets = [
    "/downloads",
    "/data/downloads",
    "/media",
    "/mnt/media",
    "/tv",
    "/movies"
  ];

  const windowsPresets = [
    "C:\\Downloads",
    "D:\\Downloads",
    "Z:\\",
    "\\\\server\\share\\media",
    "\\\\nas\\media"
  ];

  return (
    <>
      <div className={cn("flex items-center gap-2", className)}>
        <Input value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} />
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="shrink-0"
          onClick={() => void handleDiagnostic(value)}
          disabled={!value.trim() || diagnosticLoading}
        >
          {diagnosticLoading ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <FolderCheck className="h-4 w-4" />}
          Check
        </Button>
        <Button type="button" variant="outline" size="sm" className="shrink-0" onClick={() => setOpen(true)}>
          <FolderOpen className="h-4 w-4" />
          Browse
        </Button>
      </div>
      {diagnostic || diagnosticError ? (
        <PathDiagnosticPanel diagnostic={diagnostic} error={diagnosticError} compact />
      ) : null}

      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="right" className="flex max-w-2xl flex-col gap-0 p-0" aria-label={browseTitle}>
          <div className="border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.85)]">
            <div className="flex items-start justify-between gap-4 pr-10">
              <div className="space-y-1">
                <p className="text-[11px] uppercase tracking-[0.18em] text-muted-foreground">Filesystem</p>
                <h2 className="font-display text-xl font-semibold tracking-tight text-foreground">
                  {browseTitle}
                </h2>
                <p className="text-sm text-muted-foreground">
                  Browse folders visible to the Deluno server, or enter a Docker, NAS, UNC, or mounted path manually.
                </p>
              </div>
              {browserData?.currentPath ? (
                <Button type="button" size="sm" onClick={() => handleSelect(browserData.currentPath!)}>
                  Use this folder
                </Button>
              ) : null}
            </div>
          </div>

          <div className="border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
            <div className="mb-3 grid gap-2 md:grid-cols-3">
              <PathModeCard
                icon={Server}
                title="Server browse"
                copy="Shows drives and folders the Deluno backend can access."
              />
              <PathModeCard
                icon={TerminalSquare}
                title="Docker / container"
                copy="Use container paths like /downloads or /media when Deluno runs in Docker."
              />
              <PathModeCard
                icon={Network}
                title="Network / UNC"
                copy="Use paths such as \\\\nas\\media when the server account can access them."
              />
            </div>

            <div className="mb-3 rounded-2xl border border-hairline bg-surface-1 p-3">
              <div className="flex flex-col gap-2 sm:flex-row">
                <Input
                  value={manualPath}
                  onChange={(event) => setManualPath(event.target.value)}
                  placeholder="Type or paste a path, e.g. /data/media or \\\\nas\\media"
                  className="font-mono"
                />
                <Button type="button" onClick={handleManualUse} disabled={!manualPath.trim()} className="shrink-0">
                  Use typed path
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => void handleDiagnostic(manualPath)}
                  disabled={!manualPath.trim() || diagnosticLoading}
                  className="shrink-0"
                >
                  {diagnosticLoading ? (
                    <LoaderCircle className="h-4 w-4 animate-spin" />
                  ) : (
                    <FolderCheck className="h-4 w-4" />
                  )}
                  Check
                </Button>
              </div>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {[...dockerPresets, ...windowsPresets].map((presetPath) => (
                  <Button
                    key={presetPath}
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => setManualPath(presetPath)}
                  >
                    {presetPath}
                  </Button>
                ))}
              </div>
              <p className="mt-2 text-xs leading-relaxed text-muted-foreground">
                Important: Deluno validates and imports from the path as seen by the server process, not necessarily your browser machine.
              </p>
              {diagnostic || diagnosticError ? (
                <PathDiagnosticPanel diagnostic={diagnostic} error={diagnosticError} className="mt-3" />
              ) : null}
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => setBrowserPath(browserData?.parentPath ?? null)}
                disabled={loading || browserData?.currentPath === null || !browserData?.parentPath}
              >
                <ChevronLeft className="h-4 w-4" />
                Up
              </Button>
              <Button
                type="button"
                size="sm"
                variant="ghost"
                onClick={() => setBrowserPath(null)}
                disabled={loading || browserData?.currentPath === null}
              >
                <HardDrive className="h-4 w-4" />
                Roots
              </Button>
              <div className="min-w-0 flex-1 rounded-[10px] border border-hairline bg-surface-1 px-3 py-2 text-sm text-muted-foreground">
                <span className="block truncate">{browserData?.currentPath ?? "Computer roots"}</span>
              </div>
            </div>
            {notice ? (
              <p className="mt-2 rounded-xl border border-warning/25 bg-warning/10 px-3 py-2 text-sm text-warning">
                {notice}
              </p>
            ) : null}
          </div>

          <div className="flex-1 overflow-y-auto px-[var(--tile-pad)] py-[var(--tile-pad)]">
            {loading ? (
              <div className="flex h-full min-h-56 items-center justify-center rounded-2xl border border-dashed border-hairline bg-surface-1 text-sm text-muted-foreground">
                <LoaderCircle className="mr-2 h-4 w-4 animate-spin" />
                Loading folders...
              </div>
            ) : error ? (
              <div className="rounded-2xl border border-dashed border-destructive/30 bg-destructive/5 px-4 py-5 text-sm text-destructive">
                {error}
              </div>
            ) : browserData && browserData.entries.length > 0 ? (
              <div className="space-y-2">
                {browserData.entries.map((entry) => (
                  <div
                    key={entry.path}
                    className="flex items-center justify-between gap-3 rounded-2xl border border-hairline bg-surface-1 px-[calc(var(--tile-pad)*0.8)] py-[calc(var(--tile-pad)*0.7)]"
                  >
                    <button
                      type="button"
                      className="flex min-w-0 flex-1 items-center gap-3 text-left"
                      onClick={() => setBrowserPath(entry.path)}
                    >
                      <div className="rounded-xl border border-hairline bg-surface-2 p-2 text-muted-foreground">
                        {entry.kind === "root" ? (
                          <HardDrive className="h-4 w-4" />
                        ) : entry.kind === "preset" ? (
                          <Server className="h-4 w-4" />
                        ) : (
                          <Folder className="h-4 w-4" />
                        )}
                      </div>
                      <div className="min-w-0">
                        <p className="truncate text-sm font-medium text-foreground">{entry.name}</p>
                        <p className="truncate text-xs text-muted-foreground">
                          {entry.description ?? entry.path}
                        </p>
                      </div>
                    </button>
                    <div className="flex items-center gap-2">
                      <Button type="button" size="sm" variant="ghost" onClick={() => setBrowserPath(entry.path)}>
                        Open
                      </Button>
                      <Button type="button" size="sm" variant="outline" onClick={() => handleSelect(entry.path)}>
                        Select
                      </Button>
                    </div>
                  </div>
                ))}
              </div>
            ) : (
              <div className="rounded-2xl border border-dashed border-hairline bg-surface-1 px-4 py-5 text-sm text-muted-foreground">
                No folders found at this location.
              </div>
            )}
          </div>
        </SheetContent>
      </Sheet>
    </>
  );
}

function PathDiagnosticPanel({
  className,
  compact = false,
  diagnostic,
  error
}: {
  className?: string;
  compact?: boolean;
  diagnostic: PathDiagnosticResponse | null;
  error: string | null;
}) {
  if (error) {
    return (
      <div
        className={cn(
          "rounded-xl border border-destructive/25 bg-destructive/5 px-3 py-2 text-sm text-destructive",
          className
        )}
      >
        {error}
      </div>
    );
  }

  if (!diagnostic) {
    return null;
  }

  const healthy = diagnostic.exists && diagnostic.isDirectory && diagnostic.readable;
  const statusIcon = healthy ? CheckCircle2 : diagnostic.exists ? AlertTriangle : XCircle;
  const StatusIcon = statusIcon;
  const statusClass = healthy ? "text-success" : diagnostic.exists ? "text-warning" : "text-destructive";

  return (
    <div
      className={cn(
        "rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-sm",
        compact ? "mt-2" : null,
        className
      )}
    >
      <div className="flex items-start gap-2">
        <StatusIcon className={cn("mt-0.5 h-4 w-4 shrink-0", statusClass)} />
        <div className="min-w-0 flex-1">
          <p className="font-medium text-foreground">{diagnostic.message}</p>
          <p className="mt-1 truncate font-mono text-xs text-muted-foreground">{diagnostic.normalizedPath}</p>
        </div>
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5">
        <DiagnosticBadge ok={diagnostic.exists} label="Exists" />
        <DiagnosticBadge ok={diagnostic.isDirectory} label="Directory" />
        <DiagnosticBadge ok={diagnostic.readable} label="Readable" />
        <DiagnosticBadge ok={diagnostic.writable} label="Writable" />
        {diagnostic.isUncPath ? <DiagnosticBadge ok label="UNC" /> : null}
        {diagnostic.isLikelyDockerPath ? <DiagnosticBadge ok label="Docker path" /> : null}
      </div>
      {diagnostic.warnings.length > 0 ? (
        <ul className="mt-2 space-y-1 text-xs leading-relaxed text-muted-foreground">
          {diagnostic.warnings.map((warning) => (
            <li key={warning}>{warning}</li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function DiagnosticBadge({ label, ok }: { label: string; ok: boolean }) {
  return (
    <span
      className={cn(
        "rounded-full border px-2 py-0.5 text-[11px]",
        ok
          ? "border-success/25 bg-success/10 text-success"
          : "border-hairline bg-surface-2 text-muted-foreground"
      )}
    >
      {label}
    </span>
  );
}

function PathModeCard({
  icon: Icon,
  title,
  copy
}: {
  icon: typeof Server;
  title: string;
  copy: string;
}) {
  return (
    <div className="rounded-2xl border border-hairline bg-surface-1 p-3">
      <div className="flex items-center gap-2">
        <span className="flex h-8 w-8 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
          <Icon className="h-4 w-4" />
        </span>
        <p className="text-sm font-semibold text-foreground">{title}</p>
      </div>
      <p className="mt-2 text-xs leading-relaxed text-muted-foreground">{copy}</p>
    </div>
  );
}
