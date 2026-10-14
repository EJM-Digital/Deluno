import { useEffect, useState } from "react";
import { formatBytes } from "../../lib/utils";
import { Download, FolderOpen, Loader2, RotateCcw, Trash2 } from "lucide-react";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable, ListEmpty, LIST_TRACK } from "../ui/list-card";
import { toast } from "../shell/toaster";
import { fetchJson } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";

interface LogFileItem {
  name: string;
  sizeBytes: number;
  lastWrittenUtc: string;
}

/**
 * A Log Files list with download and open-folder actions (#867). Radarr's
 * own System > Logs page is the model: DelunoFileLogger already rolls and
 * retains daily log files under {DataRoot}/logs, but nothing here ever
 * listed, downloaded, or deleted one.
 */
export function LogFilesCard() {
  const [files, setFiles] = useState<LogFileItem[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [folderPath, setFolderPath] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<LogFileItem | null>(null);

  async function load() {
    try {
      const [items, folder] = await Promise.all([
        fetchJson<LogFileItem[]>("/api/system/log-files"),
        fetchJson<{ path: string }>("/api/system/log-files/folder")
      ]);
      setFiles(items);
      setFolderPath(folder.path);
      setLoadFailed(false);
    } catch {
      setLoadFailed(true);
    }
  }

  useEffect(() => {
    void load();
  }, []);

  async function download(file: LogFileItem) {
    setBusy(`download:${file.name}`);
    try {
      const response = await authedFetch(`/api/system/log-files/${encodeURIComponent(file.name)}/download`);
      if (!response.ok) throw new Error("The file could not be downloaded.");
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = file.name;
      link.click();
      URL.revokeObjectURL(url);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The file could not be downloaded.");
    } finally {
      setBusy(null);
    }
  }

  async function remove(file: LogFileItem) {
    setBusy(`delete:${file.name}`);
    try {
      const response = await authedFetch(`/api/system/log-files/${encodeURIComponent(file.name)}`, { method: "DELETE" });
      if (!response.ok) {
        const body = await response.json().catch(() => null) as { errors?: Record<string, string[]> } | null;
        throw new Error(body?.errors?.name?.[0] ?? "The file could not be deleted.");
      }
      toast.success(`${file.name} deleted`);
      await load();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The file could not be deleted.");
    } finally {
      setBusy(null);
      setConfirmDelete(null);
    }
  }

  async function copyFolderPath() {
    if (!folderPath) return;
    try {
      await navigator.clipboard.writeText(folderPath);
      toast.success("Logs folder path copied");
    } catch {
      toast.info(folderPath);
    }
  }

  return (
    <ListCard
      title="Log files"
      count={files ? `${files.length} ${files.length === 1 ? "file" : "files"}, ${formatBytes(files.reduce((sum, file) => sum + file.sizeBytes, 0))} in all` : undefined}
      actions={
        <div className="flex items-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={() => void load()}>
            <RotateCcw className="h-3.5 w-3.5" />
            Refresh
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={() => void copyFolderPath()} disabled={!folderPath}>
            <FolderOpen className="h-3.5 w-3.5" />
            Open logs folder
          </Button>
        </div>
      }
    >
      {files === null ? (
        <ListEmpty title={loadFailed ? "Log files could not be loaded" : "Loading…"} description={folderPath ?? undefined} />
      ) : files.length === 0 ? (
        <ListEmpty title="No log files yet" description={folderPath ? `Files will appear under ${folderPath}.` : undefined} />
      ) : (
        <ListTable
          listId="log-files"
          items={files}
          columns={[
            { id: "file", label: "File", sort: (file) => file.name.toLowerCase() },
            { id: "size", label: "Size", width: "120px", sort: (file) => file.sizeBytes },
            { id: "lastWritten", label: "Last written", sort: (file) => new Date(file.lastWrittenUtc) },
            { id: "actions", label: "Actions", width: LIST_TRACK.status, mobile: true, movable: false, sortable: false }
          ]}
          chevron={false}
        >
          {(rows) => rows.map((file) => (
            <ListRow key={file.name}>
              <ListNameCell name={file.name} />
              <ListCell numeric primary={formatBytes(file.sizeBytes)} />
              <ListCell primary={relative(file.lastWrittenUtc)} />
              <ListCell mobile>
                <div className="flex items-center gap-1">
                  <Button type="button" variant="outline" size="sm" aria-label={`Download ${file.name}`} onClick={() => void download(file)} disabled={busy !== null}>
                    {busy === `download:${file.name}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Download className="h-3.5 w-3.5" />}
                  </Button>
                  <Button type="button" variant="outline" size="sm" aria-label={`Delete ${file.name}`} onClick={() => setConfirmDelete(file)} disabled={busy !== null}>
                    {busy === `delete:${file.name}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5" />}
                  </Button>
                </div>
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      )}
      <ConfirmDialog
        open={confirmDelete !== null}
        onOpenChange={(open) => { if (!open) setConfirmDelete(null); }}
        title={`Delete "${confirmDelete?.name}"?`}
        description="This log file is removed from disk. This cannot be undone."
        confirmLabel="Delete file"
        busy={busy === `delete:${confirmDelete?.name}`}
        onConfirm={() => { if (confirmDelete) void remove(confirmDelete); }}
      />
    </ListCard>
  );
}

function relative(iso: string): string {
  const minutes = Math.round(Math.abs(Date.now() - new Date(iso).getTime()) / 60000);
  return minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`;
}
