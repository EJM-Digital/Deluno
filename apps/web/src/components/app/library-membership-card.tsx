import * as Dialog from "../ui/dialog-primitives";
import { Library } from "lucide-react";
import { useState } from "react";
import { readValidationProblem, type LibraryItem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { Field } from "../ui/field";
import { LIST_TRACK, ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { Select } from "../ui/select";
import { STATUS_PRESENTATION } from "../../lib/status-tones";

/** Mirrors `Deluno.Worker.MediaLibraryMembershipItem`. */
export interface LibraryMembership {
  libraryId: string;
  libraryName: string;
  hasFile: boolean;
  wantedStatus: string;
  canRemove: boolean;
  canMove: boolean;
  /** Its file here was deleted, so Deluno stopped wanting the film in this library only (#1065). */
  notWantedHere?: boolean;
}

type Pending = { kind: "keep" } | { kind: "move"; from: LibraryMembership };

/**
 * The libraries a title is in (#530), chosen by a person.
 *
 * A film can be kept in a second library on purpose - an HD and a 4K copy -
 * and taken out of one it holds nothing in. A show is in one library and can
 * be moved. With more than one library, this is also where the page's Search,
 * Choose a release and Force re-download are pointed: each goes through one
 * library's indexers and clients.
 */
export function LibraryMembershipCard({
  route,
  mediaId,
  noun,
  libraries,
  memberships,
  selectedLibraryId,
  onSelect,
  onChanged
}: {
  route: "/api/movies" | "/api/series";
  mediaId: string;
  noun: "film" | "show";
  libraries: LibraryItem[];
  memberships: LibraryMembership[];
  selectedLibraryId: string | null;
  onSelect: (libraryId: string) => void;
  onChanged: () => void;
}) {
  const [pending, setPending] = useState<Pending | null>(null);
  const [target, setTarget] = useState("");
  const [removing, setRemoving] = useState<LibraryMembership | null>(null);
  const [busy, setBusy] = useState(false);

  const others = libraries.filter((library) => !memberships.some((membership) => membership.libraryId === library.id));
  const canKeepElsewhere = noun === "film" && others.length > 0;

  async function send(path: string, method: "POST" | "PUT" | "DELETE", body: unknown, done: string) {
    setBusy(true);
    try {
      const response = await authedFetch(path, {
        method,
        headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined
      });
      if (!response.ok) {
        const problem = await readValidationProblem(response.clone()).catch(() => null);
        const message = problem?.errors ? Object.values(problem.errors)[0]?.[0] : ((await response.json().catch(() => null)) as { message?: string } | null)?.message;
        toast.error(message ?? `That could not be done (HTTP ${response.status}).`);
        return false;
      }

      toast.success(done);
      onChanged();
      return true;
    } catch {
      toast.error("That could not be done. Deluno could not be reached.");
      return false;
    } finally {
      setBusy(false);
    }
  }

  function open(next: Pending) {
    setPending(next);
    setTarget(others[0]?.id ?? "");
  }

  async function confirm() {
    if (!pending || !target) return;
    const name = libraries.find((library) => library.id === target)?.name ?? "that library";
    const ok = pending.kind === "keep"
      ? await send(`${route}/${mediaId}/libraries/`, "POST", { libraryId: target }, `Also kept in ${name}.`)
      : await send(`${route}/${mediaId}/libraries/${pending.from.libraryId}`, "PUT", { libraryId: target }, `Moved to ${name}.`);
    if (ok) {
      setPending(null);
      if (pending.kind === "move" && selectedLibraryId === pending.from.libraryId) onSelect(target);
    }
  }

  return (
    <>
      <ListCard
        title="Libraries"
        count={memberships.length > 1 ? `in ${memberships.length}` : undefined}
        actions={canKeepElsewhere ? <Button type="button" variant="outline" size="sm" onClick={() => open({ kind: "keep" })} disabled={busy}>Keep in another library…</Button> : undefined}
      >
        <ListTable
          chevron={false}
          listId="library-membership"
          items={memberships}
          columns={[
            { id: "library", label: "Library", sort: (membership) => membership.libraryName.toLowerCase() },
            { id: "copy", label: "Copy", sort: (membership) => (membership.notWantedHere ? 2 : membership.hasFile ? 0 : 1) },
            { id: "actions", label: "", width: LIST_TRACK.action, align: "end", mobile: true, srOnly: true }
          ]}
        >
          {(rows) => rows.map((membership) => {
            const chip = membership.hasFile ? STATUS_PRESENTATION["transfer.imported"] : { tone: "idle" as const, label: "No file" };
            const stoppedHere = membership.notWantedHere === true;
            return (
              <ListRow key={membership.libraryId}>
                <ListNameCell name={membership.libraryName} sub={stoppedHere ? "Its file was deleted, so Deluno stopped wanting it here. The other copy is still managed." : memberships.length > 1 && membership.libraryId === selectedLibraryId ? "Search and download go through this library" : undefined} />
                <ListCell primary={<Chip tone={chip.tone}>{stoppedHere ? "Not wanted here" : membership.hasFile ? "Has a file" : chip.label}</Chip>} />
                <div role="cell" className="flex justify-end gap-2">
                  {stoppedHere ? (
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      aria-label={`Want it again in ${membership.libraryName}`}
                      onClick={() => void send(`${route}/${mediaId}/libraries/${membership.libraryId}/want-again`, "POST", null, `Deluno wants it again in ${membership.libraryName}.`)}
                      disabled={busy}
                    >
                      Want it again
                    </Button>
                  ) : null}
                  {memberships.length > 1 && membership.libraryId !== selectedLibraryId ? (
                    <Button type="button" size="sm" variant="ghost" onClick={() => onSelect(membership.libraryId)} disabled={busy}>Use for search</Button>
                  ) : null}
                  {membership.canMove && others.length > 0 ? (
                    <Button type="button" size="sm" variant="outline" onClick={() => open({ kind: "move", from: membership })} disabled={busy}>Move…</Button>
                  ) : null}
                  {membership.canRemove ? (
                    <Button type="button" size="sm" variant="ghost" onClick={() => setRemoving(membership)} disabled={busy}>Remove</Button>
                  ) : null}
                </div>
              </ListRow>
            );
          })}
        </ListTable>
      </ListCard>

      <Dialog.Root open={pending !== null} onOpenChange={(next) => { if (!next) setPending(null); }}>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
          <Dialog.Content
            className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl data-[state=open]:animate-fade-in"
            aria-describedby="library-membership-description"
          >
            <div className="border-b border-hairline px-6 py-5">
              <div className="flex items-start gap-3">
                <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary/10">
                  <Library className="h-4 w-4 text-primary" />
                </span>
                <div>
                  <Dialog.Title className="font-semibold text-foreground">
                    {pending?.kind === "move" ? `Move out of ${pending.from.libraryName}?` : `Keep this ${noun} in another library too?`}
                  </Dialog.Title>
                  <Dialog.Description id="library-membership-description" className="mt-1 text-(length:--type-body-sm) leading-relaxed text-muted-foreground">
                    {pending?.kind === "move"
                      ? `Its searches and downloads will go through the library you choose. It holds no file in ${pending.from.libraryName}, so nothing on disk moves.`
                      : "It will be wanted there as a separate copy, with that library's quality, indexers and download clients."}
                  </Dialog.Description>
                </div>
              </div>
            </div>
            <div className="grid gap-(--grid-gap) p-6">
              <Field label="Library">
                <Select value={target} onChange={(event) => setTarget(event.target.value)} options={others.map((library) => ({ value: library.id, label: library.name }))} />
              </Field>
            </div>
            <div className="flex justify-end gap-2 border-t border-hairline bg-surface-1/60 px-6 py-4">
              <Dialog.Close asChild>
                <Button type="button" variant="ghost" disabled={busy}>Cancel</Button>
              </Dialog.Close>
              <Button type="button" onClick={() => void confirm()} disabled={busy || !target}>
                {pending?.kind === "move" ? "Move" : "Keep it there too"}
              </Button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>

      <ConfirmDialog
        open={removing !== null}
        onOpenChange={(next) => {
          if (!next) setRemoving(null);
        }}
        title={`Stop wanting it in ${removing?.libraryName ?? "this library"}?`}
        description={`It holds no file there. It stays in its other ${memberships.length - 1 === 1 ? "library" : "libraries"}.`}
        confirmLabel="Remove from library"
        busy={busy}
        onConfirm={() => {
          if (!removing) return;
          void send(`${route}/${mediaId}/libraries/${removing.libraryId}`, "DELETE", null, `No longer in ${removing.libraryName}.`).then((ok) => {
            if (ok) {
              if (selectedLibraryId === removing.libraryId) onSelect(memberships.find((membership) => membership.libraryId !== removing.libraryId)?.libraryId ?? "");
              setRemoving(null);
            }
          });
        }}
      />
    </>
  );
}
